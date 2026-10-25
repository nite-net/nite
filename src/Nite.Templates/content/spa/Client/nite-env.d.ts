// Types for Nite's built-in modules.
declare module "nite:config" {
  /**
   * The values NiteOptions.ClientConfig returns in Program.cs, written into each page by the
   * server (camelCase). Describe them here so `import { config } from "nite:config"` is typed.
   */
  export interface NiteClientConfig {}

  export const config: Readonly<NiteClientConfig>;
  export default config;
}
