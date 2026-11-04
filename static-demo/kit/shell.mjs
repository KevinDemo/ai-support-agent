// Build helpers shared by the static demo pages. No dependencies, so a build needs only Node.
import { cpSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

export const BANNER = "Static demo running in your browser. The real .NET service is in the repo.";

export const esc = (value) => String(value ?? "")
  .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

/** Clears the output folder and copies the theme into it. themeDir holds theme.css and theme.js. */
export function startOutput(out, themeDir) {
  rmSync(out, { recursive: true, force: true });
  mkdirSync(out, { recursive: true });
  writeFileSync(join(out, ".nojekyll"), "");
  cpSync(join(themeDir, "theme.css"), join(out, "theme.css"));
  cpSync(join(themeDir, "theme.js"), join(out, "theme.js"));
}

/** One complete page. Every asset path is relative, so it works under /<repo>/ on GitHub Pages. */
export function page({ repo, product, title, lead, body, styles = [], scripts = [], nav = "" }) {
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${esc(title)} - Harbour Freight Couriers (fictional)</title>
<meta name="description" content="${esc(lead)}">
<meta name="color-scheme" content="light dark">
<meta name="theme-color" media="(prefers-color-scheme: light)" content="#f6f6f4">
<meta name="theme-color" media="(prefers-color-scheme: dark)" content="#0a0b0c">
<script>
(function () {
  var root = document.documentElement;
  root.classList.add("js");
  try {
    var saved = localStorage.getItem("theme");
    if (saved === "light" || saved === "dark") root.dataset.theme = saved;
  } catch (e) {}
})();
</script>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Geist:wght@400..700&family=Geist+Mono:wght@400..600&display=swap">
<link rel="stylesheet" href="theme.css">
${styles.map((s) => `<link rel="stylesheet" href="${s}">`).join("\n")}
</head>
<body>
<header class="topbar">
  <div class="wrap topbar-row">
    <a class="mark" href="https://kevindemo.github.io/" target="_blank" rel="noopener"><span class="mark-box" aria-hidden="true">KC</span>Kevin Chen</a>
    <span class="product">${esc(product)}</span>
    <nav aria-label="Links">
      ${nav}
      <a href="https://github.com/KevinDemo/${repo}" target="_blank" rel="noopener">Source on GitHub</a>
    </nav>
    <fieldset class="theme">
      <legend class="sr">Theme</legend>
      <label><input type="radio" name="theme" value="system" checked><span>System</span></label>
      <label><input type="radio" name="theme" value="light"><span>Light</span></label>
      <label><input type="radio" name="theme" value="dark"><span>Dark</span></label>
    </fieldset>
  </div>
</header>
<div class="banner-band"><p class="wrap banner" role="note">${BANNER}</p></div>
<div class="wrap sheet">
${body}
</div>
<script src="theme.js"></script>
${scripts.map((s) => `<script type="module" src="${s}"></script>`).join("\n")}
</body>
</html>
`;
}
