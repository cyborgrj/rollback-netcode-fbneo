// ---------------------------------------------------------------------------
// port_map.h - Ask the home router to open the UDP port, via UPnP IGD.
//
// This is the piece Fightcade leans on ("forward ggpo ports 6000-6009, or
// enable upnp"), and it covers a case hole punching cannot: a router whose NAT
// remaps per destination. An explicit mapping is not per-destination, so the
// endpoint the rendezvous observes is reachable from anywhere, and the pair
// stays on a direct connection instead of falling back to the relay.
//
// What it does NOT fix is carrier-grade NAT. You can ask your own router for a
// mapping; you cannot ask your operator's. Mobile and CGNAT connections still
// need the relay - see nat_punch.h.
//
// Protocol, all of it plain text over sockets we already link:
//   1. SSDP  - multicast M-SEARCH to 239.255.255.250:1900, keep the LOCATION
//   2. HTTP  - GET that URL, the device description XML
//   3. SOAP  - POST AddPortMapping to the WANIPConnection control URL
//
// Threading: call from the emulation thread before opening the session. Blocks
// for at most a couple of seconds, budgeted so a router that ignores us costs a
// short pause and nothing else.
// ---------------------------------------------------------------------------
#ifndef ROLLBACK_PORT_MAP_H
#define ROLLBACK_PORT_MAP_H

#ifdef __cplusplus
extern "C" {
#endif

enum PortMapResult {
	PORT_MAP_OK           =  0,
	PORT_MAP_ERR_ARG      = -1,
	PORT_MAP_ERR_NO_IGD   = -2,   // no router answered the search
	PORT_MAP_ERR_DESC     = -3,   // could not read/parse the device description
	PORT_MAP_ERR_REFUSED  = -4    // the router answered, and said no
};

// Map UDP nPort on the router to this machine's nPort. Idempotent from our side:
// a second call replaces the mapping. pfnLog is optional.
int  PortMapOpenUdp(unsigned short nPort, void (*pfnLog)(const char*));

// Remove whatever PortMapOpenUdp last created. Safe to call unconditionally.
void PortMapClose(void);

// 1 while a mapping we made is believed to be in place.
int  PortMapIsOpen(void);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // ROLLBACK_PORT_MAP_H
