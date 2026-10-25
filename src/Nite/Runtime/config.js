// nite:config — what NiteOptions.ClientConfig returned for this page, read once when the page loads.
const element = document.getElementById("nite-config");
export const config = Object.freeze(element ? JSON.parse(element.textContent) : {});
export default config;
