// Types for Nite's built-in modules.
declare module "nite:config" {
  /** What NiteOptions.ClientConfig returns in Program.cs, written into each page by the server. */
  export interface NiteClientConfig {
    /** The Application Insights connection string; absent unless the AppHost has one. */
    appInsights?: string;
    server: {
      environment: string;
      machine: string;
      /** The trace of the request that served this page. */
      traceId?: string;
      renderedAt: string;
    };
  }

  export const config: Readonly<NiteClientConfig>;
  export default config;
}
