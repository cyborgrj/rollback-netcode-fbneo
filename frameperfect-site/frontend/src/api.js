/**
 * Cliente da API.
 *
 * O token de acesso fica em memoria, nao em localStorage: qualquer script que
 * consiga rodar na pagina le o localStorage, e um token roubado vale ate
 * expirar. O refresh mora em localStorage porque ele precisa sobreviver ao
 * recarregar - e a troca consciente que essa decisao pede.
 */

let accessToken = null;

const BASE = "/api";

async function req(caminho, { metodo = "GET", corpo, auth = true } = {}) {
  const headers = { "Content-Type": "application/json" };
  if (auth && accessToken) headers.Authorization = `Bearer ${accessToken}`;

  const r = await fetch(`${BASE}${caminho}`, {
    method: metodo,
    headers,
    body: corpo ? JSON.stringify(corpo) : undefined,
  });

  if (r.status === 401 && auth && localStorage.getItem("fp_refresh")) {
    if (await renovar()) return req(caminho, { metodo, corpo, auth });
  }

  const dados = r.status === 204 ? null : await r.json().catch(() => null);
  if (!r.ok) {
    // A mensagem do servidor, quando ela existe, ja e escrita para ser lida por
    // uma pessoa. Inventar outra aqui so afastaria o texto do problema.
    const erro = new Error(dados?.detalhe || dados?.detail || `Erro ${r.status}`);
    erro.status = r.status;
    erro.campos = dados;
    throw erro;
  }
  return dados;
}

async function renovar() {
  const refresh = localStorage.getItem("fp_refresh");
  if (!refresh) return false;
  try {
    const r = await req("/auth/refresh", { metodo: "POST", corpo: { refresh }, auth: false });
    accessToken = r.access;
    if (r.refresh) localStorage.setItem("fp_refresh", r.refresh);
    return true;
  } catch {
    localStorage.removeItem("fp_refresh");
    accessToken = null;
    return false;
  }
}

export const api = {
  async entrar(username, password) {
    const r = await req("/auth/login", { metodo: "POST", corpo: { username, password }, auth: false });
    accessToken = r.access;
    localStorage.setItem("fp_refresh", r.refresh);
    return r;
  },
  sair() {
    accessToken = null;
    localStorage.removeItem("fp_refresh");
  },
  registrar: (username, email, password) =>
    req("/auth/register", { metodo: "POST", corpo: { username, email, password }, auth: false }),
  confirmar: (codigo) => req(`/auth/confirmar/${codigo}`, { metodo: "POST", auth: false }),
  eu: () => req("/me"),
  excluirConta: () => req("/me/excluir", { metodo: "POST" }),
  minhasPartidas: () => req("/partidas"),
  meusRivais: () => req("/rivais"),
  restaurarSessao: renovar,
};
