import { useEffect, useRef } from "react";
import * as THREE from "three";

/**
 * O gabinete de arcade que gira conforme a página rola.
 *
 * Three.js direto, num useEffect, em vez de react-three-fiber: a cena é
 * estática e a única coisa que muda é a rotação. R3F pagaria o custo de
 * reconciliação do React sessenta vezes por segundo para não resolver nada
 * que este componente já não resolva em quarenta linhas.
 *
 * A silhueta vem de um perfil extrudado, não de uma caixa. O painel de
 * controle inclinado e o ombro sob a marquise são o que fazem a forma ser
 * reconhecível como fliperama.
 */
export default function Cabinet() {
  const hostRef = useRef(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    const reduzirMovimento = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(34, 1, 0.1, 100);
    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    host.appendChild(renderer.domElement);

    const perfil = new THREE.Shape();
    perfil.moveTo(-0.9, -2.4);
    perfil.lineTo(0.9, -2.4);
    perfil.lineTo(0.9, -0.55);
    perfil.lineTo(0.45, -0.3);
    perfil.lineTo(0.45, 0.55);
    perfil.lineTo(0.9, 0.95);
    perfil.lineTo(0.9, 2.05);
    perfil.lineTo(-0.9, 2.05);
    perfil.closePath();

    const gabinete = new THREE.Group();

    const corpo = new THREE.Mesh(
      new THREE.ExtrudeGeometry(perfil, {
        depth: 1.7, bevelEnabled: true,
        bevelSize: 0.045, bevelThickness: 0.045, bevelSegments: 2,
      }),
      new THREE.MeshStandardMaterial({ color: 0x1a1e30, roughness: 0.72, metalness: 0.15 })
    );
    corpo.position.z = -0.85;
    gabinete.add(corpo);

    // A tela mostra o próprio overlay do emulador. É o que amarra o objeto 3D
    // ao produto - um gabinete com a tela apagada seria só um móvel.
    const telaCanvas = document.createElement("canvas");
    telaCanvas.width = 320;
    telaCanvas.height = 224;
    const ctx = telaCanvas.getContext("2d");
    const textura = new THREE.CanvasTexture(telaCanvas);

    const tela = new THREE.Mesh(
      new THREE.PlaneGeometry(1.5, 1.05),
      new THREE.MeshBasicMaterial({ map: textura })
    );
    tela.position.set(0, 0.62, 0.87);
    gabinete.add(tela);

    const painel = new THREE.Mesh(
      new THREE.BoxGeometry(1.75, 0.1, 0.92),
      new THREE.MeshStandardMaterial({ color: 0x11131f, roughness: 0.8 })
    );
    painel.position.set(0, -0.42, 0.42);
    painel.rotation.x = -0.3;
    gabinete.add(painel);

    const bola = new THREE.Mesh(
      new THREE.SphereGeometry(0.085, 18, 14),
      new THREE.MeshStandardMaterial({ color: 0xff5c8a, roughness: 0.35 })
    );
    bola.position.set(-0.58, -0.26, 0.52);
    gabinete.add(bola);

    // Seis botões em duas fileiras: o arranjo de jogo de luta, não três botões
    // genéricos.
    for (let linha = 0; linha < 2; linha++) {
      for (let col = 0; col < 3; col++) {
        const botao = new THREE.Mesh(
          new THREE.CylinderGeometry(0.062, 0.062, 0.04, 16),
          new THREE.MeshStandardMaterial({
            color: linha ? 0xc08cff : 0x7fe8ff, roughness: 0.35,
          })
        );
        botao.rotation.x = Math.PI / 2 - 0.3;
        botao.position.set(0.05 + col * 0.2, -0.27 - linha * 0.135, 0.6 - linha * 0.2);
        gabinete.add(botao);
      }
    }

    scene.add(gabinete);
    scene.add(new THREE.AmbientLight(0x3a3f5c, 1.1));
    const luzP1 = new THREE.PointLight(0x7fe8ff, 1.5, 22);
    luzP1.position.set(-4, 3, 5);
    scene.add(luzP1);
    const luzP2 = new THREE.PointLight(0xc08cff, 1.3, 22);
    luzP2.position.set(4, -1, 4);
    scene.add(luzP2);

    let alvo = 0;
    let atual = 0;
    let t = 0;
    let vivo = true;

    const aoRolar = () => {
      const el = document.documentElement;
      const max = el.scrollHeight - el.clientHeight;
      alvo = max > 0 ? el.scrollTop / max : 0;
    };

    const redimensionar = () => {
      const w = host.clientWidth;
      const h = host.clientHeight;
      if (!w || !h) return;
      renderer.setSize(w, h, false);
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
    };

    const desenharTela = (vida1, vida2) => {
      ctx.fillStyle = "#0A0C14";
      ctx.fillRect(0, 0, 320, 224);

      ctx.fillStyle = "rgba(20,23,38,.94)";
      ctx.fillRect(0, 0, 320, 20);
      ctx.font = '13px "VT323", monospace';
      ctx.textBaseline = "middle";
      ctx.textAlign = "right";
      ctx.fillStyle = "#7FE8FF";
      ctx.fillText("CYBORGRJ", 132, 10);
      ctx.fillStyle = "#fff";
      ctx.fillText("3", 146, 10);
      ctx.textAlign = "center";
      ctx.fillStyle = "#8287AE";
      ctx.fillText("FT5", 160, 10);
      ctx.textAlign = "left";
      ctx.fillStyle = "#fff";
      ctx.fillText("1", 174, 10);
      ctx.fillStyle = "#C08CFF";
      ctx.fillText("GLIVISON", 188, 10);

      ctx.fillStyle = "#262B49";
      ctx.fillRect(14, 30, 132, 11);
      ctx.fillRect(174, 30, 132, 11);
      ctx.fillStyle = "#7FE8FF";
      ctx.fillRect(14, 30, 132 * vida1, 11);
      ctx.fillStyle = "#C08CFF";
      ctx.fillRect(174 + 132 * (1 - vida2), 30, 132 * vida2, 11);

      ctx.fillStyle = "#7FE8FF";
      ctx.fillRect(96, 140, 16, 44);
      ctx.fillRect(92, 126, 24, 14);
      ctx.fillStyle = "#C08CFF";
      ctx.fillRect(208, 140, 16, 44);
      ctx.fillRect(204, 126, 24, 14);
      ctx.fillStyle = "#1B1F33";
      ctx.fillRect(0, 184, 320, 40);

      textura.needsUpdate = true;
    };

    const quadro = () => {
      if (!vivo) return;
      atual += (alvo - atual) * 0.07;
      t += 0.016;

      gabinete.rotation.y = -0.5 + atual * 2.4 + (reduzirMovimento ? 0 : Math.sin(t * 0.5) * 0.05);
      gabinete.rotation.x = 0.06 - atual * 0.12;
      camera.position.set(0, 0.25 + atual * 0.6, 6.6 - atual * 1.4);
      camera.lookAt(0, 0.35 - atual * 0.5, 0);

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
      host.removeChild(renderer.domElement);
    };
  }, []);

  return (
    <div
      ref={hostRef}
      role="img"
      aria-label="Gabinete de arcade em 3D girando conforme a página rola"
      className="relative w-full aspect-[3/4] max-h-[560px] overflow-hidden rounded-md border border-line bg-panel"
    />
  );
}
