/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "../**/*.{razor,html,cshtml}",
    "./index.html",
    "./js/**/*.js"
  ],
  theme: {
    extend: {
      colors: {
        ink: "#070b14",
        gold: "#ff6a1a",
        cream: "#e8eef8",
        cyan: "#2ee6ff"
      },
      fontFamily: {
        serif: ["Rajdhani", "sans-serif"],
        sans: ["Outfit", "sans-serif"]
      }
    }
  },
  plugins: []
};
