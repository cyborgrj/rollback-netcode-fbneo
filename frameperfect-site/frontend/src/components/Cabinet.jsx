import { useEffect, useRef } from "react";
import * as THREE from "three";

/**
 * O gabinete de arcade se abrindo conforme a página rola.
 *
 * A coreografia tem três tempos, e cada um responde a uma faixa do scroll:
 *
 *   0.00 – 0.22   montado, girando devagar
 *   0.22 – 0.68   as peças se afastam e as entranhas aparecem: o tubo do CRT,
 *                 a placa, a fonte, o alto-falante e o chicote de fios
 *   0.68 – 1.00   a câmera entra no meio das peças e o logo vem de longe
 *
 * Three.js direto num useEffect, e não react-three-fiber: a cena é montada uma
 * vez e o que muda a cada quadro é a posição de vinte objetos. R3F pagaria a
 * reconciliação do React sessenta vezes por segundo para não resolver nada que
 * este arquivo já não resolva.
 */

const CIANO = 0x7fe8ff;
const LILAS = 0xc08cff;

/** Interpola de 0 a 1 dentro de uma faixa, com suavização nas pontas. */
function faixa(p, inicio, fim) {
  const t = Math.min(1, Math.max(0, (p - inicio) / (fim - inicio)));
  return t * t * (3 - 2 * t);
}

export default function Cabinet() {
  const hostRef = useRef(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    const reduzir = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(36, 1, 0.1, 200);
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    host.appendChild(renderer.domElement);

    const gabinete = new THREE.Group();
    scene.add(gabinete);

    /** Cada peça guarda para onde ela vai quando abre. */
    const pecas = [];
    function add(mesh, destino, giro) {
      mesh.userData.base = mesh.position.clone();
      mesh.userData.destino = new THREE.Vector3(...destino);
      mesh.userData.giro = giro || 0;
      gabinete.add(mesh);
      pecas.push(mesh);
      return mesh;
    }

    // ---- materiais -------------------------------------------------------
    const matCasco = new THREE.MeshStandardMaterial({ color: 0x1a1e30, roughness: 0.75, metalness: 0.12 });
    const matPreto = new THREE.MeshStandardMaterial({ color: 0x0e1018, roughness: 0.9 });
    const matMetal = new THREE.MeshStandardMaterial({ color: 0x5a6180, roughness: 0.42, metalness: 0.72 });
    const matPlaca = new THREE.MeshStandardMaterial({ color: 0x1d4a34, roughness: 0.68 });
    const matChip  = new THREE.MeshStandardMaterial({ color: 0x14161f, roughness: 0.55 });
    const matVidro = new THREE.MeshStandardMaterial({
      color: 0x0a0c14, roughness: 0.18, metalness: 0.1,
      emissive: CIANO, emissiveIntensity: 0.06,
    });

    // ---- casco: as laterais vêm de um perfil, não de caixas --------------
    // É o painel inclinado e o ombro sob a marquise que fazem a silhueta ser
    // reconhecível como fliperama; uma caixa leria como caixa.
    const perfil = new THREE.Shape();
    perfil.moveTo(-0.95, -2.5);
    perfil.lineTo(0.95, -2.5);
    perfil.lineTo(0.95, -0.6);
    perfil.lineTo(0.42, -0.32);
    perfil.lineTo(0.42, 0.6);
    perfil.lineTo(0.95, 1.0);
    perfil.lineTo(0.95, 2.15);
    perfil.lineTo(-0.95, 2.15);
    perfil.closePath();

    const geoLateral = new THREE.ExtrudeGeometry(perfil, {
      depth: 0.09, bevelEnabled: true, bevelSize: 0.02, bevelThickness: 0.02, bevelSegments: 1,
    });

    const ladoE = new THREE.Mesh(geoLateral, matCasco);
    ladoE.position.set(0, 0, -0.9);
    add(ladoE, [-2.6, 0, -0.5], -0.25);

    const ladoD = new THREE.Mesh(geoLateral, matCasco);
    ladoD.position.set(0, 0, 0.81);
    add(ladoD, [2.6, 0, 0.5], 0.25);

    // fundo, topo e base fecham a caixa
    add(new THREE.Mesh(new THREE.BoxGeometry(1.72, 4.65, 0.08), matPreto)
        .translateX(-0.93).translateY(-0.18), [-1.3, 0, -2.4], 0);
    add(new THREE.Mesh(new THREE.BoxGeometry(1.72, 0.08, 1.8), matCasco)
        .translateY(2.11), [0, 2.2, 0], 0);
    add(new THREE.Mesh(new THREE.BoxGeometry(1.72, 0.08, 1.8), matPreto)
        .translateY(-2.46), [0, -1.6, 0], 0);

    // ---- tela e moldura --------------------------------------------------
    const telaCanvas = document.createElement("canvas");
    telaCanvas.width = 320;
    telaCanvas.height = 224;
    const ctx = telaCanvas.getContext("2d");
    const texTela = new THREE.CanvasTexture(telaCanvas);

    const tela = new THREE.Mesh(
      new THREE.PlaneGeometry(1.42, 1.0),
      new THREE.MeshBasicMaterial({ map: texTela })
    );
    tela.rotation.y = Math.PI / 2;
    tela.position.set(0.36, 0.72, 0);
    add(tela, [1.4, 0.3, 0], 0);

    const moldura = new THREE.Mesh(
      new THREE.BoxGeometry(0.06, 1.36, 1.72), matPreto
    );
    moldura.position.set(0.4, 0.72, 0);
    add(moldura, [2.9, 0.5, 0], 0.1);

    // ---- marquise --------------------------------------------------------
    const mc = document.createElement("canvas");
    mc.width = 512; mc.height = 128;
    const mx = mc.getContext("2d");
    const texMarquise = new THREE.CanvasTexture(mc);
    const marquise = new THREE.Mesh(
      new THREE.PlaneGeometry(1.7, 0.44),
      new THREE.MeshBasicMaterial({ map: texMarquise, transparent: true })
    );
    marquise.rotation.y = Math.PI / 2;
    marquise.position.set(0.44, 1.74, 0);
    add(marquise, [1.2, 1.5, 0], 0);

    // ---- painel de controle ---------------------------------------------
    const painel = new THREE.Group();
    const tampo = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.07, 1.72), matPreto);
    tampo.rotation.z = 0.3;
    painel.add(tampo);

    const bola = new THREE.Mesh(
      new THREE.SphereGeometry(0.082, 18, 14),
      new THREE.MeshStandardMaterial({ color: 0xff5c8a, roughness: 0.3 })
    );
    bola.position.set(0.06, 0.16, -0.55);
    painel.add(bola);
    const haste = new THREE.Mesh(new THREE.CylinderGeometry(0.02, 0.02, 0.16, 10), matMetal);
    haste.position.set(0.05, 0.07, -0.55);
    painel.add(haste);

    // Seis botões em duas fileiras: o arranjo de jogo de luta, não três
    // botões genéricos que serviriam para qualquer máquina.
    for (let linha = 0; linha < 2; linha++) {
      for (let col = 0; col < 3; col++) {
        const b = new THREE.Mesh(
          new THREE.CylinderGeometry(0.058, 0.058, 0.035, 16),
          new THREE.MeshStandardMaterial({
            color: linha ? LILAS : CIANO, roughness: 0.3,
            emissive: linha ? LILAS : CIANO, emissiveIntensity: 0.25,
          })
        );
        b.position.set(0.09 - linha * 0.05, 0.06, -0.12 + col * 0.22);
        painel.add(b);
      }
    }
    painel.position.set(0.2, -0.4, 0);
    add(painel, [2.2, -1.5, 0], 0.2);

    // ---- entranhas -------------------------------------------------------
    // O tubo: face, funil e pescoço. É a peça que mais diz "arcade por dentro".
    const crt = new THREE.Group();
    const face = new THREE.Mesh(new THREE.CylinderGeometry(0.62, 0.62, 0.1, 24), matVidro);
    face.rotation.z = Math.PI / 2;
    crt.add(face);
    const funil = new THREE.Mesh(new THREE.CylinderGeometry(0.6, 0.14, 0.85, 24, 1, true), matPreto);
    funil.rotation.z = Math.PI / 2;
    funil.position.x = -0.48;
    funil.material.side = THREE.DoubleSide;
    crt.add(funil);
    const pescoco = new THREE.Mesh(new THREE.CylinderGeometry(0.075, 0.075, 0.42, 14), matMetal);
    pescoco.rotation.z = Math.PI / 2;
    pescoco.position.x = -1.08;
    crt.add(pescoco);
    crt.position.set(-0.1, 0.72, 0);
    add(crt, [-0.2, 1.1, 0], 0);

    // A placa, com chips e conector de borda.
    const placa = new THREE.Group();
    const board = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.9, 1.2), matPlaca);
    placa.add(board);
    for (let i = 0; i < 7; i++) {
      const chip = new THREE.Mesh(
        new THREE.BoxGeometry(0.045, 0.09 + (i % 3) * 0.05, 0.2 + (i % 2) * 0.12), matChip
      );
      chip.position.set(0.05, 0.3 - (i % 4) * 0.2, -0.42 + Math.floor(i / 4) * 0.5);
      placa.add(chip);
    }
    const conector = new THREE.Mesh(new THREE.BoxGeometry(0.07, 0.09, 1.05), matMetal);
    conector.position.set(0, -0.48, 0);
    placa.add(conector);
    placa.position.set(-0.45, -0.5, 0);
    add(placa, [-2.3, -0.4, 0.6], -0.5);

    // Fonte de alimentação.
    const fonte = new THREE.Mesh(new THREE.BoxGeometry(0.34, 0.5, 0.9), matMetal);
    fonte.position.set(-0.4, -1.5, 0);
    add(fonte, [-1.9, -2.1, -0.7], 0.35);

    // Alto-falante.
    const alto = new THREE.Group();
    alto.add(new THREE.Mesh(new THREE.CylinderGeometry(0.3, 0.3, 0.06, 20), matPreto));
    const cone = new THREE.Mesh(new THREE.CylinderGeometry(0.26, 0.09, 0.16, 20, 1, true), matChip);
    cone.material.side = THREE.DoubleSide;
    cone.position.y = -0.1;
    alto.add(cone);
    alto.rotation.z = Math.PI / 2;
    alto.position.set(0.2, 1.35, 0.55);
    add(alto, [1.6, 2.0, 1.2], 0.4);

    // O chicote de fios. Tubos curvos em ciano e lilás - é o detalhe que faz a
    // vista aberta parecer uma máquina desmontada e não peças flutuando.
    [
      [[-0.4, -1.2, 0.2], [-0.2, -0.4, 0.5], [-0.2, 0.5, 0.2], CIANO],
      [[-0.45, -1.3, -0.2], [-0.6, -0.2, -0.5], [-0.3, 0.6, -0.3], LILAS],
      [[-0.3, -1.1, 0], [0.1, -0.7, 0.35], [0.15, -0.42, 0.1], 0xff5c8a],
    ].forEach(([a, b, c, cor]) => {
      const curva = new THREE.CatmullRomCurve3([
        new THREE.Vector3(...a), new THREE.Vector3(...b), new THREE.Vector3(...c),
      ]);
      const fio = new THREE.Mesh(
        new THREE.TubeGeometry(curva, 26, 0.022, 7, false),
        new THREE.MeshStandardMaterial({ color: cor, roughness: 0.5, emissive: cor, emissiveIntensity: 0.18 })
      );
      add(fio, [-1.4, -0.6, 0.9], 0);
    });

    // ---- o logo que vem de longe ----------------------------------------
    const lc = document.createElement("canvas");
    lc.width = 1024; lc.height = 256;
    const lx = lc.getContext("2d");
    const texLogo = new THREE.CanvasTexture(lc);
    const logo = new THREE.Mesh(
      new THREE.PlaneGeometry(4.6, 1.15),
      new THREE.MeshBasicMaterial({
        map: texLogo, transparent: true, depthWrite: false,
        blending: THREE.AdditiveBlending, opacity: 0,
      })
    );
    scene.add(logo);

    function desenharNeon(alvo, texto1, texto2, tam, comGlow) {
      alvo.clearRect(0, 0, 1024, 256);
      alvo.textAlign = "center";
      alvo.textBaseline = "middle";
      alvo.font = `${tam}px "Press Start 2P", monospace`;

      // Neon de verdade é o mesmo traço desenhado várias vezes com desfoque
      // crescente. Uma sombra só dá contorno, não brilho.
      const camadas = comGlow ? [[34, 0.30], [18, 0.55], [7, 0.9], [0, 1]] : [[0, 1]];
      for (const [blur, alpha] of camadas) {
        alvo.globalAlpha = alpha;
        alvo.shadowBlur = blur;

        alvo.shadowColor = "#7FE8FF";
        alvo.fillStyle = blur ? "#7FE8FF" : "#DFF8FF";
        alvo.fillText(texto1, 512, comGlow ? 92 : 40);

        alvo.shadowColor = "#C08CFF";
        alvo.fillStyle = blur ? "#C08CFF" : "#F0E4FF";
        alvo.fillText(texto2, 512, comGlow ? 176 : 96);
      }
      alvo.globalAlpha = 1;
      alvo.shadowBlur = 0;
    }

    // A fonte precisa ter carregado antes de o canvas desenhar com ela, senão
    // sai no fallback e ninguém entende por quê.
    const pintarTexturas = () => {
      desenharNeon(lx, "FRAME", "PERFECT", 86, true);
      texLogo.needsUpdate = true;

      mx.clearRect(0, 0, 512, 128);
      mx.fillStyle = "#0A0C14";
      mx.fillRect(0, 0, 512, 128);
      desenharNeon(mx, "FRAME", "PERFECT", 34, false);
      texMarquise.needsUpdate = true;
    };
    pintarTexturas();
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(pintarTexturas);

    // ---- luz -------------------------------------------------------------
    // Direcionais, e nao pontuais: a partir do three 0.155 a intensidade e
    // fisicamente correta e uma PointLight cai com o quadrado da distancia, o
    // que a cinco unidades de distancia nao ilumina praticamente nada. Luz
    // direcional nao decai, entao o valor que se escreve e o que se ve.
    scene.add(new THREE.AmbientLight(0x39406b, 2.4));

    const luz1 = new THREE.DirectionalLight(CIANO, 3.2);
    luz1.position.set(-6, 5, 8);
    scene.add(luz1);

    const luz2 = new THREE.DirectionalLight(LILAS, 2.4);
    luz2.position.set(7, -2, 5);
    scene.add(luz2);

    // Uma contraluz quente por tras, so para as bordas das pecas nao sumirem
    // no fundo quando o gabinete abre.
    const luz3 = new THREE.DirectionalLight(0xff5c8a, 1.1);
    luz3.position.set(0, -3, -6);
    scene.add(luz3);

    // ---- a tela ligada ---------------------------------------------------
    function desenharTela(v1, v2) {
      ctx.fillStyle = "#0A0C14";
      ctx.fillRect(0, 0, 320, 224);
      ctx.fillStyle = "rgba(20,23,38,.94)";
      ctx.fillRect(0, 0, 320, 20);
      ctx.font = '13px "VT323", monospace';
      ctx.textBaseline = "middle";
      ctx.textAlign = "right";
      ctx.fillStyle = "#7FE8FF"; ctx.fillText("CYBORGRJ", 132, 10);
      ctx.fillStyle = "#fff";    ctx.fillText("3", 146, 10);
      ctx.textAlign = "center";
      ctx.fillStyle = "#8287AE"; ctx.fillText("FT5", 160, 10);
      ctx.textAlign = "left";
      ctx.fillStyle = "#fff";    ctx.fillText("1", 174, 10);
      ctx.fillStyle = "#C08CFF"; ctx.fillText("GLIVISON", 188, 10);

      ctx.fillStyle = "#262B49";
      ctx.fillRect(14, 30, 132, 11);
      ctx.fillRect(174, 30, 132, 11);
      ctx.fillStyle = "#7FE8FF"; ctx.fillRect(14, 30, 132 * v1, 11);
      ctx.fillStyle = "#C08CFF"; ctx.fillRect(174 + 132 * (1 - v2), 30, 132 * v2, 11);

      ctx.fillStyle = "#7FE8FF";
      ctx.fillRect(96, 140, 16, 44); ctx.fillRect(92, 126, 24, 14);
      ctx.fillStyle = "#C08CFF";
      ctx.fillRect(208, 140, 16, 44); ctx.fillRect(204, 126, 24, 14);
      ctx.fillStyle = "#1B1F33"; ctx.fillRect(0, 184, 320, 40);
      texTela.needsUpdate = true;
    }

    // ---- laço ------------------------------------------------------------
    let alvo = 0, atual = 0, t = 0, vivo = true;

    const aoRolar = () => {
      const el = document.documentElement;
      const max = el.scrollHeight - el.clientHeight;
      alvo = max > 0 ? el.scrollTop / max : 0;
    };

    const redimensionar = () => {
      const w = host.clientWidth, h = host.clientHeight;
      if (!w || !h) return;
      renderer.setSize(w, h, false);
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
    };

    const tmp = new THREE.Vector3();

    const quadro = () => {
      if (!vivo) return;
      atual += (alvo - atual) * 0.075;
      t += 0.016;

      const abrir = faixa(atual, 0.22, 0.68);
      const entrar = faixa(atual, 0.68, 1.0);

      for (const m of pecas) {
        tmp.copy(m.userData.base).lerp(
          tmp.copy(m.userData.base).add(m.userData.destino), abrir
        );
        m.position.copy(tmp);
        if (m.userData.giro) m.rotation.z = m.userData.giro * abrir;
        // As peças de fora somem no fim, para não tapar as entranhas.
        if (m.material && m.material.transparent === false) m.material.transparent = false;
      }

      // A frente do gabinete e o +X do perfil, entao a rotacao inicial precisa
      // traze-la para a camera - sem isto o visitante abre a pagina olhando
      // para a lateral lisa.
      // A frente do gabinete e o +X do perfil. Girado por theta em torno de Y
      // ele aponta para (cos, 0, -sen), entao trazer a frente para a camera
      // (que olha de +Z) pede angulo NEGATIVO. Com o sinal trocado o visitante
      // abre a pagina olhando para dentro da maquina pelo fundo.
      gabinete.rotation.y = -1.0 + atual * 2.6 + (reduzir ? 0 : Math.sin(t * 0.45) * 0.04);
      gabinete.rotation.x = 0.05 - abrir * 0.16;

      camera.position.set(0, 0.35 + abrir * 0.5, 7.6 - abrir * 1.4 - entrar * 2.6);
      camera.lookAt(0, 0.15 - abrir * 0.2, 0);

      // O logo vem de muito longe e para na frente da câmera.
      const z = -26 + entrar * 24;
      logo.position.set(0, 0.35, z);
      logo.quaternion.copy(camera.quaternion);
      logo.material.opacity = entrar;
      const s = 0.4 + entrar * 0.9;
      logo.scale.set(s, s, s);

      desenharTela(0.62 + Math.sin(t * 0.7) * 0.08, 0.34 + Math.cos(t * 0.9) * 0.1);
      renderer.render(scene, camera);
      requestAnimationFrame(quadro);
    };

    window.addEventListener("scroll", aoRolar, { passive: true });
    window.addEventListener("resize", redimensionar);
    redimensionar();
    aoRolar();
    quadro();

    return () => {
      vivo = false;
      window.removeEventListener("scroll", aoRolar);
      window.removeEventListener("resize", redimensionar);
      renderer.dispose();
      if (renderer.domElement.parentNode === host) host.removeChild(renderer.domElement);
    };
  }, []);

  return (
    <div
      ref={hostRef}
      role="img"
      aria-label="Gabinete de arcade em 3D que se abre conforme a página rola, mostrando o tubo, a placa e a fonte"
      className="relative w-full aspect-[3/4] max-h-[600px] overflow-hidden rounded-md border border-line bg-panel"
    />
  );
}
