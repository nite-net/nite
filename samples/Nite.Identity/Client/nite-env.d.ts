// Types for Nite's built-in modules.
declare module "nite:config" {
  /**
   * What NiteOptions.ClientConfig returns in Program.cs, written into each page by the server.
   * Signed-in users get their claims and a bearer token for the API; the login page, served to
   * everyone, gets them only when someone is already signed in.
   */
  export interface NiteClientConfig {
    user?: {
      name: string;
      roles: string[];
      claims: { type: string; value: string }[];
    };
    /** Send as `Authorization: Bearer <token>` to /api. */
    token?: string;
    tokenExpires?: string;
  }

  export const config: Readonly<NiteClientConfig>;
  export default config;
}
