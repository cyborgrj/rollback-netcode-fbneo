export default {
  content: ["./index.html", "./src/**/*.{js,jsx}"],
  theme: {
    extend: {
      colors: {
        ground: "#0A0C14",
        panel: "#141726",
        panel2: "#1B1F33",
        line: "#262B49",
        ink: "#EAE8F5",
        dim: "#8287AE",
        p1: "#7FE8FF",
        p2: "#C08CFF",
        ko: "#FF5C8A",
      },
      fontFamily: {
        pixel: ['"Press Start 2P"', "monospace"],
        display: ['"VT323"', "monospace"],
        sans: ['"Archivo"', "system-ui", "sans-serif"],
      },
    },
  },
  plugins: [],
};
