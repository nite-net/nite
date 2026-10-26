// Types for Nite's built-in modules.
declare module "nite:config" {
  /**
   * What NiteOptions.ClientConfig returns in Program.cs, written into each page by the server.
   * There is no credential here: the session is an HttpOnly cookie that scripts cannot read.
   */
  export interface NiteClientConfig {
    /** Sent back in the X-CSRF-Token header on requests that change data. */
    csrfToken: string;
    /** Who is signed in; absent on the login page for signed-out visitors. */
    user?: {
      name: string;
      roles: string[];
      claims: { type: string; value: string }[];
    };
  }

  export const config: Readonly<NiteClientConfig>;
  export default config;
}
