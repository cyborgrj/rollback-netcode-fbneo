// ---------------------------------------------------------------------------
// port_map.cpp - see port_map.h for the reasoning and the protocol.
// ---------------------------------------------------------------------------
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "port_map.h"

#include <stdio.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

#define PM_SSDP_ADDR      "239.255.255.250"
#define PM_SSDP_PORT      1900
#define PM_SSDP_WAIT_MS   1200   // routers answer in well under a second
#define PM_HTTP_TIMEOUT   2500
#define PM_DESC_MAX       65536
#define PM_REPLY_MAX      8192
#define PM_LEASE_SECONDS  7200   // renewed every match; 0 (permanent) as fallback

static void pm_log(void (*pfnLog)(const char*), const char* fmt, ...)
{
	if (!pfnLog) return;
	char buf[400];
	va_list ap;
	va_start(ap, fmt);
	_vsnprintf(buf, sizeof(buf) - 1, fmt, ap);
	va_end(ap);
	buf[sizeof(buf) - 1] = '\0';
	pfnLog(buf);
}

// What we need to talk to the router again at PortMapClose time.
static struct {
	int            open;
	int            wsa;          // we hold a WSAStartup reference
	char           szControlUrl[512];
	char           szServiceType[128];
	unsigned short nPort;
} G;

// ---------------------------------------------------------------------------
// tiny string helpers - the XML here is small and utterly predictable, so a
// real parser would be more code than it is worth
// ---------------------------------------------------------------------------
static const char* ci_find(const char* hay, const char* needle)
{
	if (!hay || !needle || !*needle) return NULL;
	size_t n = strlen(needle);
	for (const char* p = hay; *p; p++)
		if (_strnicmp(p, needle, n) == 0) return p;
	return NULL;
}

// Copy what sits between two markers. Returns 0 on success.
static int between(const char* src, const char* open, const char* close, char* out, int cap)
{
	const char* a = ci_find(src, open);
	if (!a) return -1;
	a += strlen(open);
	const char* b = ci_find(a, close);
	if (!b || b <= a) return -1;
	int n = (int)(b - a);
	if (n >= cap) n = cap - 1;
	memcpy(out, a, n);
	out[n] = '\0';
	return 0;
}

// http://192.168.0.1:5000/rootDesc.xml -> host, port, path
static int splitUrl(const char* url, char* host, int hostCap, unsigned short* port, char* path, int pathCap)
{
	if (_strnicmp(url, "http://", 7) != 0) return -1;
	const char* p = url + 7;

	const char* slash = strchr(p, '/');
	const char* colon = strchr(p, ':');
	if (colon && slash && colon > slash) colon = NULL;

	const char* hostEnd = colon ? colon : (slash ? slash : p + strlen(p));
	int n = (int)(hostEnd - p);
	if (n <= 0 || n >= hostCap) return -1;
	memcpy(host, p, n);
	host[n] = '\0';

	*port = colon ? (unsigned short)atoi(colon + 1) : 80;
	if (*port == 0) return -1;

	_snprintf(path, pathCap - 1, "%s", slash ? slash : "/");
	path[pathCap - 1] = '\0';
	return 0;
}

// ---------------------------------------------------------------------------
// sockets
// ---------------------------------------------------------------------------
static SOCKET tcpTo(const char* host, unsigned short port)
{
	char szPort[16];
	_snprintf(szPort, sizeof(szPort) - 1, "%u", (unsigned)port);
	szPort[sizeof(szPort) - 1] = '\0';

	struct addrinfo hints, *ai = NULL;
	memset(&hints, 0, sizeof(hints));
	hints.ai_family   = AF_INET;
	hints.ai_socktype = SOCK_STREAM;
	if (getaddrinfo(host, szPort, &hints, &ai) != 0 || !ai) return INVALID_SOCKET;

	SOCKET s = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
	if (s == INVALID_SOCKET) { freeaddrinfo(ai); return INVALID_SOCKET; }

	DWORD to = PM_HTTP_TIMEOUT;
	setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&to, sizeof(to));
	setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char*)&to, sizeof(to));

	int rc = connect(s, ai->ai_addr, (int)ai->ai_addrlen);
	freeaddrinfo(ai);
	if (rc != 0) { closesocket(s); return INVALID_SOCKET; }
	return s;
}

static int sendAll(SOCKET s, const char* p, int n)
{
	while (n > 0) {
		int k = send(s, p, n, 0);
		if (k <= 0) return -1;
		p += k;
		n -= k;
	}
	return 0;
}

// Read until the peer closes or the buffer fills. Routers close after each
// response, so this needs no Content-Length handling.
static int recvAll(SOCKET s, char* out, int cap)
{
	int got = 0;
	while (got < cap - 1) {
		int k = recv(s, out + got, cap - 1 - got, 0);
		if (k <= 0) break;
		got += k;
	}
	out[got] = '\0';
	return got;
}

// Our address on the route towards the router - the value the mapping has to
// point at. No packet is sent; connecting a UDP socket only picks a route.
static int localIpToward(const char* host, char* out, int cap)
{
	SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
	if (s == INVALID_SOCKET) return -1;

	struct addrinfo hints, *ai = NULL;
	memset(&hints, 0, sizeof(hints));
	hints.ai_family   = AF_INET;
	hints.ai_socktype = SOCK_DGRAM;
	if (getaddrinfo(host, "9", &hints, &ai) != 0 || !ai) { closesocket(s); return -1; }

	int rc = connect(s, ai->ai_addr, (int)ai->ai_addrlen);
	freeaddrinfo(ai);
	if (rc != 0) { closesocket(s); return -1; }

	struct sockaddr_in me;
	int nMe = sizeof(me);
	if (getsockname(s, (struct sockaddr*)&me, &nMe) != 0) { closesocket(s); return -1; }
	closesocket(s);

	return inet_ntop(AF_INET, &me.sin_addr, out, cap) ? 0 : -1;
}

// ---------------------------------------------------------------------------
// 1. SSDP: who here is an internet gateway?
// ---------------------------------------------------------------------------
static int ssdpDiscover(char* outLocation, int cap, void (*pfnLog)(const char*))
{
	SOCKET s = socket(AF_INET, SOCK_DGRAM, 0);
	if (s == INVALID_SOCKET) return PORT_MAP_ERR_NO_IGD;

	struct sockaddr_in to;
	memset(&to, 0, sizeof(to));
	to.sin_family = AF_INET;
	to.sin_port   = htons(PM_SSDP_PORT);
	inet_pton(AF_INET, PM_SSDP_ADDR, &to.sin_addr);

	// Two searches: the gateway device, and the service we actually want. Some
	// routers answer only one of them.
	static const char* kTargets[] = {
		"urn:schemas-upnp-org:device:InternetGatewayDevice:1",
		"urn:schemas-upnp-org:service:WANIPConnection:1",
	};

	for (int i = 0; i < 2; i++) {
		char req[512];
		int n = _snprintf(req, sizeof(req) - 1,
			"M-SEARCH * HTTP/1.1\r\n"
			"HOST: %s:%d\r\n"
			"MAN: \"ssdp:discover\"\r\n"
			"MX: 1\r\n"
			"ST: %s\r\n\r\n",
			PM_SSDP_ADDR, PM_SSDP_PORT, kTargets[i]);
		if (n > 0) sendto(s, req, n, 0, (struct sockaddr*)&to, sizeof(to));
	}

	DWORD tStart = GetTickCount();
	while ((int)(GetTickCount() - tStart) < PM_SSDP_WAIT_MS) {
		fd_set rf;
		FD_ZERO(&rf);
		FD_SET(s, &rf);
		struct timeval tv;
		tv.tv_sec  = 0;
		tv.tv_usec = 200 * 1000;
		if (select(0, &rf, NULL, NULL, &tv) <= 0) continue;

		char buf[2048];
		int n = recv(s, buf, sizeof(buf) - 1, 0);
		if (n <= 0) continue;
		buf[n] = '\0';

		// Ignore anything that is not a gateway - printers and TVs answer too.
		if (!ci_find(buf, "InternetGatewayDevice") && !ci_find(buf, "WANIPConnection") &&
		    !ci_find(buf, "WANPPPConnection"))
			continue;

		if (between(buf, "LOCATION:", "\r\n", outLocation, cap) != 0 &&
		    between(buf, "Location:", "\r\n", outLocation, cap) != 0)
			continue;

		// Trim the space after the header name.
		char* p = outLocation;
		while (*p == ' ' || *p == '\t') p++;
		if (p != outLocation) memmove(outLocation, p, strlen(p) + 1);

		closesocket(s);
		pm_log(pfnLog, "upnp: router description at %s", outLocation);
		return PORT_MAP_OK;
	}

	closesocket(s);
	pm_log(pfnLog, "upnp: no router answered the search");
	return PORT_MAP_ERR_NO_IGD;
}

// ---------------------------------------------------------------------------
// 2. the device description, and the control URL inside it
// ---------------------------------------------------------------------------
static int fetchDescription(const char* url, char* out, int cap)
{
	char host[256], path[512];
	unsigned short port = 0;
	if (splitUrl(url, host, sizeof(host), &port, path, sizeof(path)) != 0) return -1;

	SOCKET s = tcpTo(host, port);
	if (s == INVALID_SOCKET) return -1;

	char req[1024];
	int n = _snprintf(req, sizeof(req) - 1,
		"GET %s HTTP/1.1\r\nHost: %s:%u\r\nConnection: close\r\n\r\n", path, host, (unsigned)port);
	int rc = (n > 0 && sendAll(s, req, n) == 0) ? 0 : -1;
	if (rc == 0) rc = recvAll(s, out, cap) > 0 ? 0 : -1;
	closesocket(s);
	return rc;
}

// Walk the <service> blocks and take the first WAN connection service, keeping
// its type and control URL together - they must match, and the order of the
// child elements is not the same on every router.
static int findWanService(const char* xml, char* szType, int nType, char* szControl, int nControl)
{
	const char* p = xml;
	while ((p = ci_find(p, "<service>")) != NULL) {
		const char* end = ci_find(p, "</service>");
		if (!end) break;

		int len = (int)(end - p);
		char block[2048];
		if (len >= (int)sizeof(block)) len = sizeof(block) - 1;
		memcpy(block, p, len);
		block[len] = '\0';

		if (ci_find(block, "WANIPConnection") || ci_find(block, "WANPPPConnection")) {
			if (between(block, "<serviceType>", "</serviceType>", szType, nType) == 0 &&
			    between(block, "<controlURL>", "</controlURL>", szControl, nControl) == 0)
				return 0;
		}
		p = end + 1;
	}
	return -1;
}

// ---------------------------------------------------------------------------
// 3. SOAP
// ---------------------------------------------------------------------------
static int soapCall(const char* szControlUrl, const char* szServiceType,
                    const char* szAction, const char* szArgs,
                    char* outReply, int nReply)
{
	char host[256], path[512];
	unsigned short port = 0;
	if (splitUrl(szControlUrl, host, sizeof(host), &port, path, sizeof(path)) != 0) return -1;

	char body[1400];
	int nBody = _snprintf(body, sizeof(body) - 1,
		"<?xml version=\"1.0\"?>"
		"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
		"s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
		"<s:Body><u:%s xmlns:u=\"%s\">%s</u:%s></s:Body></s:Envelope>",
		szAction, szServiceType, szArgs, szAction);
	if (nBody <= 0) return -1;

	char head[1024];
	int nHead = _snprintf(head, sizeof(head) - 1,
		"POST %s HTTP/1.1\r\n"
		"Host: %s:%u\r\n"
		"Content-Type: text/xml; charset=\"utf-8\"\r\n"
		"SOAPAction: \"%s#%s\"\r\n"
		"Content-Length: %d\r\n"
		"Connection: close\r\n\r\n",
		path, host, (unsigned)port, szServiceType, szAction, nBody);
	if (nHead <= 0) return -1;

	SOCKET s = tcpTo(host, port);
	if (s == INVALID_SOCKET) return -1;

	int rc = (sendAll(s, head, nHead) == 0 && sendAll(s, body, nBody) == 0) ? 0 : -1;
	if (rc == 0) rc = recvAll(s, outReply, nReply) > 0 ? 0 : -1;
	closesocket(s);

	if (rc != 0) return -1;
	return ci_find(outReply, " 200 ") ? 0 : -1;
}

// ---------------------------------------------------------------------------
// public
// ---------------------------------------------------------------------------
int PortMapOpenUdp(unsigned short nPort, void (*pfnLog)(const char*))
{
	if (nPort == 0) return PORT_MAP_ERR_ARG;
	PortMapClose();

	// Winsock is refcounted per process and nothing has started it yet at this
	// point - we run before the punch, which brings up its own. Same trap that
	// bit relay.cpp: without this, getaddrinfo just fails with 10093.
	WSADATA wsad;
	if (WSAStartup(MAKEWORD(2, 2), &wsad) != 0) return PORT_MAP_ERR_NO_IGD;
	G.wsa = 1;

	char szLocation[512] = "";
	int rc = ssdpDiscover(szLocation, sizeof(szLocation), pfnLog);
	if (rc != PORT_MAP_OK) { WSACleanup(); G.wsa = 0; return rc; }

	char host[256], path[512];
	unsigned short descPort = 0;
	if (splitUrl(szLocation, host, sizeof(host), &descPort, path, sizeof(path)) != 0) {
		pm_log(pfnLog, "upnp: router gave an address we cannot parse");
		return PORT_MAP_ERR_DESC;
	}

	char* xml = (char*)malloc(PM_DESC_MAX);
	if (!xml) return PORT_MAP_ERR_DESC;

	if (fetchDescription(szLocation, xml, PM_DESC_MAX) != 0) {
		free(xml);
		pm_log(pfnLog, "upnp: could not read the router description");
		return PORT_MAP_ERR_DESC;
	}

	char szType[128] = "", szControl[512] = "";
	int found = findWanService(xml, szType, sizeof(szType), szControl, sizeof(szControl));
	free(xml);
	if (found != 0) {
		pm_log(pfnLog, "upnp: router has no WAN connection service");
		return PORT_MAP_ERR_DESC;
	}

	// The control URL is usually a bare path; make it absolute against the
	// address the description came from.
	char szFullControl[512];
	if (_strnicmp(szControl, "http://", 7) == 0) {
		_snprintf(szFullControl, sizeof(szFullControl) - 1, "%s", szControl);
	} else {
		_snprintf(szFullControl, sizeof(szFullControl) - 1, "http://%s:%u%s%s",
		          host, (unsigned)descPort, szControl[0] == '/' ? "" : "/", szControl);
	}
	szFullControl[sizeof(szFullControl) - 1] = '\0';

	char szMe[64] = "";
	if (localIpToward(host, szMe, sizeof(szMe)) != 0) {
		pm_log(pfnLog, "upnp: cannot tell which of our addresses faces the router");
		return PORT_MAP_ERR_DESC;
	}

	char* reply = (char*)malloc(PM_REPLY_MAX);
	if (!reply) return PORT_MAP_ERR_DESC;

	// A lease is the polite form: if we crash, the mapping expires on its own.
	// Routers that only accept permanent mappings answer with an error, and for
	// those we ask again for a permanent one and delete it ourselves on the way out.
	int ok = -1;
	for (int attempt = 0; attempt < 2 && ok != 0; attempt++) {
		char args[768];
		_snprintf(args, sizeof(args) - 1,
			"<NewRemoteHost></NewRemoteHost>"
			"<NewExternalPort>%u</NewExternalPort>"
			"<NewProtocol>UDP</NewProtocol>"
			"<NewInternalPort>%u</NewInternalPort>"
			"<NewInternalClient>%s</NewInternalClient>"
			"<NewEnabled>1</NewEnabled>"
			"<NewPortMappingDescription>RBF rollback netcode</NewPortMappingDescription>"
			"<NewLeaseDuration>%d</NewLeaseDuration>",
			(unsigned)nPort, (unsigned)nPort, szMe, attempt == 0 ? PM_LEASE_SECONDS : 0);
		args[sizeof(args) - 1] = '\0';

		ok = soapCall(szFullControl, szType, "AddPortMapping", args, reply, PM_REPLY_MAX);
	}
	free(reply);

	if (ok != 0) {
		pm_log(pfnLog, "upnp: router refused to map udp/%u", (unsigned)nPort);
		return PORT_MAP_ERR_REFUSED;
	}

	G.open  = 1;
	G.nPort = nPort;
	_snprintf(G.szControlUrl, sizeof(G.szControlUrl) - 1, "%s", szFullControl);
	_snprintf(G.szServiceType, sizeof(G.szServiceType) - 1, "%s", szType);
	G.szControlUrl[sizeof(G.szControlUrl) - 1] = '\0';
	G.szServiceType[sizeof(G.szServiceType) - 1] = '\0';

	pm_log(pfnLog, "upnp: udp/%u mapped to %s", (unsigned)nPort, szMe);
	return PORT_MAP_OK;
}

void PortMapClose(void)
{
	if (!G.open) {
		if (G.wsa) WSACleanup();
		memset(&G, 0, sizeof(G));
		return;
	}

	char args[256];
	_snprintf(args, sizeof(args) - 1,
		"<NewRemoteHost></NewRemoteHost>"
		"<NewExternalPort>%u</NewExternalPort>"
		"<NewProtocol>UDP</NewProtocol>",
		(unsigned)G.nPort);
	args[sizeof(args) - 1] = '\0';

	char* reply = (char*)malloc(PM_REPLY_MAX);
	if (reply) {
		soapCall(G.szControlUrl, G.szServiceType, "DeletePortMapping", args, reply, PM_REPLY_MAX);
		free(reply);
	}
	if (G.wsa) WSACleanup();
	memset(&G, 0, sizeof(G));
}

int PortMapIsOpen(void) { return G.open; }
