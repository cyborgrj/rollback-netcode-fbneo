/**
 * A linha de frames de um rollback acontecendo.
 *
 * Ilustrar rollback com ícone não explica nada. Isto mostra o que de fato
 * acontece: o input do adversário some por três frames, o jogo roda no palpite,
 * e quando o input chega os três frames são refeitos com o comando certo.
 *
 * Os números são os de verdade — oito frames é o limite de previsão do libggpo,
 * e três frames de re-simulação levam menos de um milissegundo.
 */

const FRAMES = [120, 121, 122, 123, 124, 125, 126, 127];

// Índices 2,3,4 são os frames cujo input remoto atrasou; 5,6,7 são os refeitos.
const ADVERSARIO = ["ok", "ok", "gone", "gone", "gone", "ok", "ok", "ok"];
const JOGO = ["ok", "ok", "guess", "guess", "guess", "redo", "redo", "redo"];

const ESTILO = {
  ok:    "border-p1/50 bg-p1/[.07] text-p1",
  gone:  "border-ko/45 bg-ko/[.07] text-ko",
  guess: "border-dashed border-p2/50 bg-p2/[.06] text-p2",
  redo:  "border-p2/80 bg-p2/[.16] text-p2",
};

const SIMBOLO = { ok: "✓", gone: "✕", guess: "?", redo: "↺" };

function Linha({ rotulo, estados, simbolos }) {
  return (
    <>
      <div className="flex h-[34px] items-center font-pixel text-[8px] tracking-wider text-dim">
        {rotulo}
      </div>
      {estados.map((e, i) => (
        <div key={i}
             className={`flex h-[34px] items-center justify-center rounded-[3px] border font-display text-[19px] ${ESTILO[e]}`}>
          {(simbolos || SIMBOLO)[e]}
        </div>
      ))}
    </>
  );
}

export default function Timeline() {
  return (
    <div className="mt-8 overflow-x-auto rounded-md border border-line bg-panel px-6 pb-5 pt-6">
      <div className="grid min-w-[620px] grid-cols-[112px_repeat(8,1fr)] gap-1.5">
        <div className="flex h-[34px] items-center font-pixel text-[8px] tracking-wider text-dim">
          frame
        </div>
        {FRAMES.map((f) => (
          <div key={f} className="flex h-[34px] items-center justify-center font-display text-base tabular-nums text-dim">
            {f}
          </div>
        ))}

        <Linha rotulo="você" estados={Array(8).fill("ok")} />
        <Linha rotulo="adversário" estados={ADVERSARIO} />
        <Linha rotulo="o jogo roda" estados={JOGO}
               simbolos={{ ...SIMBOLO, ok: "→" }} />
      </div>

      <div className="mt-4 flex flex-wrap gap-5 text-[13px] text-dim">
        {[
          ["text-p1", "", "input que chegou"],
          ["text-ko", "", "input atrasado na rede"],
          ["text-p2", "border-dashed", "frame rodado no palpite"],
          ["text-p2", "bg-p2/50", "refeito com o comando certo"],
        ].map(([cor, extra, txt]) => (
          <span key={txt} className="inline-flex items-center gap-2">
            <span className={`h-[11px] w-[11px] rounded-sm border border-current ${cor} ${extra}`} />
            {txt}
          </span>
        ))}
      </div>

      <p className="mt-4 border-l-2 border-p2 pl-3.5 text-[13px] text-dim">
        Os três frames refeitos levam menos de um milissegundo. A tela nunca mostra o palpite
        errado — ela mostra o frame 125 já corrigido, no tempo dele.
      </p>
    </div>
  );
}
