import Cabinet from "./components/Cabinet.jsx";
import Timeline from "./components/Timeline.jsx";

const JOGOS = [
  { sn: "sf2ce", titulo: ["Street Fighter II′", "Champion Edition"], ano: "Capcom · 1992" },
  { sn: "sfa2",  titulo: ["Street Fighter", "Alpha 2"],              ano: "Capcom · 1996" },
  { sn: "vsav",  titulo: ["Vampire", "Savior"],                      ano: "Capcom · 1997" },
  { sn: "kof98", titulo: ["The King of", "Fighters ’98"],            ano: "SNK · 1998" },
];

const REDE = [
  ["Conexão direta primeiro",
   "O emulador abre a porta no seu roteador sozinho, por UPnP, e fura o NAT com o adversário. Quando dá certo, o tráfego vai máquina a máquina, sem passar por ninguém."],
  ["Relay quando não dá",
   "Internet móvel e CGNAT trocam o endereço a cada destino, e furar NAT não resolve. Aí a partida passa por um relay em São Paulo — 15 ms de desvio, não o Atlântico."],
  ["Assistir ao vivo",
   "Quem chega depois recebe o save state do começo e todos os inputs confirmados, e acelera em silêncio até alcançar. Entrar no meio da luta funciona."],
  ["Replay para baixar",
   "O mesmo fluxo que alimenta quem assiste vira o arquivo do replay. Determinístico: é a partida inteira, não um vídeo dela."],
];

function Spec({ v, k }) {
  return (
    <div className="flex-1 border-r border-line px-4 py-3.5 last:border-r-0">
      <div className="font-display text-[30px] leading-none text-p1 tabular-nums">{v}</div>
      <div className="mt-1 text-[11px] tracking-wide text-dim">{k}</div>
    </div>
  );
}

export default function App() {
  return (
    <>
      <div className="crt" aria-hidden="true" />

      <header className="sticky top-0 z-20 border-b border-line bg-ground/80 backdrop-blur">
        <div className="mx-auto flex h-[62px] max-w-[1180px] items-center gap-7 px-7">
          <a href="#top" className="whitespace-nowrap font-pixel text-xs no-underline text-ink">
            FRAME<span className="text-p1">PERFECT</span>
          </a>
          <nav className="ml-auto flex items-center gap-6">
            {[["#rollback", "Rollback"], ["#jogos", "Jogos"], ["#placar", "Placar"], ["#rede", "Rede"]].map(
              ([href, txt]) => (
                <a key={href} href={href}
                   className="hidden border-b border-transparent py-1.5 text-sm font-medium text-dim no-underline hover:border-p1 hover:text-ink md:inline">
                  {txt}
                </a>
              )
            )}
            <a href="#baixar" className="btn">Baixar</a>
          </nav>
        </div>
      </header>

      <main id="top" className="mx-auto max-w-[1180px] px-7">
        <section className="grid items-center gap-12 py-[72px] lg:grid-cols-[1.05fr_.95fr]">
          <div>
            <p className="eyebrow mb-[18px]">Emulador de arcade com rollback netcode</p>
            <h1 className="mb-5 text-[clamp(52px,8.5vw,104px)] leading-[.92]">
              O golpe sai <span className="text-p1">quando você aperta</span>.
            </h1>
            <p className="mb-4 max-w-[62ch] text-[19px] text-dim">
              Rollback netcode de verdade dentro do FinalBurn Neo. O jogo não espera o adversário:
              ele adivinha, e corrige antes de você ver.
            </p>
            <div className="mt-7 flex flex-wrap gap-3">
              <a href="#baixar" className="btn">Baixar para Windows</a>
              <a href="#rollback" className="btn btn-ghost">Como funciona</a>
            </div>
            <div className="mt-10 flex flex-wrap overflow-hidden rounded border border-line bg-panel">
              <Spec v="8" k="frames de previsão" />
              <Spec v="2" k="frames de delay" />
              <Spec v="60" k="quadros por segundo" />
              <Spec v="SP" k="relay em São Paulo" />
            </div>
          </div>
          <Cabinet />
        </section>

        <section id="rollback" className="border-t border-line py-24">
          <p className="eyebrow mb-[18px]">O que o rollback faz</p>
          <h2 className="mb-4 text-[clamp(34px,4.6vw,56px)] leading-none">
            Oito frames de palpite, corrigidos sem você perceber.
          </h2>
          <p className="mb-4 max-w-[62ch] text-[19px] text-dim">
            Netcode com delay segura o seu comando até o do outro chegar — e todo lag vira atraso no
            seu próprio joystick. Rollback faz o contrário: roda o frame na hora, chutando o que o
            adversário fez, e refaz quando o input real chega diferente.
          </p>
          <Timeline />
        </section>

        <section id="jogos" className="border-t border-line py-24">
          <p className="eyebrow mb-[18px]">Quatro jogos, para começar</p>
          <h2 className="mb-4 text-[clamp(34px,4.6vw,56px)] leading-none">
            Os clássicos que ninguém aposentou.
          </h2>
          <p className="mb-4 max-w-[62ch] text-[19px] text-dim">
            Cada um com os endereços de memória mapeados: placar e personagem lidos direto do jogo,
            não do que o jogador diz que aconteceu.
          </p>
          <div className="mt-8 grid grid-cols-2 gap-3.5 lg:grid-cols-4">
            {JOGOS.map((j) => (
              <div key={j.sn} className="flex min-h-[132px] flex-col gap-1.5 rounded border border-line bg-panel px-[18px] py-5">
                <div className="font-mono text-[11px] tracking-wide text-p2">{j.sn}</div>
                <div className="mt-auto font-display text-[25px] leading-[1.05]">
                  {j.titulo[0]}<br />{j.titulo[1]}
                </div>
                <div className="text-xs text-dim">{j.ano}</div>
              </div>
            ))}
          </div>
        </section>

        <section id="rede" className="border-t border-line py-24">
          <p className="eyebrow mb-[18px]">A parte chata da internet</p>
          <h2 className="mb-8 text-[clamp(34px,4.6vw,56px)] leading-none">
            Funciona até no 5G da operadora.
          </h2>
          <div className="grid gap-px overflow-hidden rounded border border-line bg-line sm:grid-cols-2">
            {REDE.map(([t, d]) => (
              <div key={t} className="bg-panel px-6 py-6">
                <h3 className="mb-2 text-lg font-semibold">{t}</h3>
                <p className="text-[15px] text-dim">{d}</p>
              </div>
            ))}
          </div>
        </section>
      </main>

      <footer className="mt-8 border-t border-line py-11 text-sm text-dim">
        <div className="mx-auto flex max-w-[1180px] flex-wrap items-center justify-between gap-6 px-7">
          <div>
            <div className="font-pixel text-[10px] text-ink">FRAME<span className="text-p1">PERFECT</span></div>
            <div className="mt-2">frameperfect.net · feito no Brasil</div>
          </div>
          <div>Rollback sobre FinalBurn Neo e libggpo</div>
        </div>
      </footer>
    </>
  );
}
