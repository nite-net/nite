import { config } from "nite:config";
import type { ApplicationInsights } from "@microsoft/applicationinsights-web";

let insights: ApplicationInsights | undefined;

/**
 * Starts Application Insights when the server provided a connection string. The SDK is about
 * 80 KB gzipped, so it is imported dynamically: production builds put it in its own chunk, loaded
 * after the page has rendered.
 */
export async function startTelemetry() {
  if (!config.appInsights) {
    return;
  }
  const { ApplicationInsights } = await import("@microsoft/applicationinsights-web");
  insights = new ApplicationInsights({
    config: {
      connectionString: config.appInsights,
      enableAutoRouteTracking: true,
      // Same-origin fetch calls get a W3C traceparent header by default, so /api/quote joins the
      // browser's trace on the server. Calls to other origins would need enableCorsCorrelation.
    },
  });
  insights.loadAppInsights();
  // The page view joins the trace of the request that served the page.
  if (config.server.traceId) {
    insights.context.telemetryTrace.traceID = config.server.traceId;
  }
  insights.trackPageView();
}

/** Records a custom event; does nothing when Application Insights is off. */
export function trackEvent(name: string, properties?: Record<string, unknown>) {
  insights?.trackEvent({ name }, properties);
}
