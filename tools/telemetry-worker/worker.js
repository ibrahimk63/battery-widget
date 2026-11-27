/**
 * BatteryWidget Anonymous Telemetry Worker (Cloudflare Workers - Free Tier: 100k req/day)
 * Receives zero-PII USB HID hardware IDs from users who opt in, and forwards them
 * to your private Discord channel (and/or GitHub Issues).
 */
export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") {
      return new Response(null, { status: 204 });
    }
    if (request.method !== "POST") {
      return new Response("Method Not Allowed", { status: 405 });
    }

    try {
      const text = await request.text();
      if (!text || text.length > 16384) {
        return new Response("Invalid payload size", { status: 400 });
      }

      const payload = JSON.parse(text);
      const candidates = Array.isArray(payload.UnknownHidCandidates || payload.unknownHidCandidates)
        ? (payload.UnknownHidCandidates || payload.unknownHidCandidates).slice(0, 15)
        : [];

      if (candidates.length === 0) {
        return new Response(JSON.stringify({ ok: true, skipped: "no_unknown_candidates" }), {
          status: 200,
          headers: { "content-type": "application/json" }
        });
      }

      const appVer = String(payload.AppVersion || payload.appVersion || "unknown").slice(0, 20);
      const detectedCount = Number(payload.DetectedDeviceCount ?? payload.detectedDeviceCount ?? 0);

      const normalizedCandidates = candidates.map((c) => ({
        vid: String(c.Vid || c.vid || "0x0000").slice(0, 8),
        pid: String(c.Pid || c.pid || "0x0000").slice(0, 8),
        productName: String(c.ProductName || c.productName || "Unknown").slice(0, 64),
        maxIn: Number(c.MaxIn ?? c.maxIn ?? 0),
        maxOut: Number(c.MaxOut ?? c.maxOut ?? 0),
        maxFeat: Number(c.MaxFeat ?? c.maxFeat ?? 0)
      }));

      const lines = normalizedCandidates.map((c) => {
        return `• **${c.productName}** — \`${c.vid}:${c.pid}\` (In:${c.maxIn} Out:${c.maxOut} Feat:${c.maxFeat})`;
      });

      const webhookUrl = typeof env.DISCORD_WEBHOOK_URL === "string" ? env.DISCORD_WEBHOOK_URL.trim() : "";
      if (!webhookUrl) {
        const envKeys = env && typeof env === "object" ? Object.keys(env).sort() : [];
        return new Response(JSON.stringify({ ok: false, error: "webhook_not_configured", envKeys }), {
          status: 503,
          headers: { "content-type": "application/json" }
        });
      }

      const discordResp = await fetch(webhookUrl, {
        method: "POST",
        headers: {
          "content-type": "application/json",
          "user-agent": "BatteryWidget-Telemetry/1.2"
        },
        body: JSON.stringify({
          embeds: [
            {
              title: "BatteryWidget — Yeni Anonim Cihaz Raporu",
              color: 2278750,
              description: lines.join("\n"),
              footer: { text: `v${appVer} • Algılanan cihaz sayısı: ${detectedCount}` },
              timestamp: new Date().toISOString()
            }
          ]
        })
      });

      if (!discordResp.ok) {
        return new Response(JSON.stringify({
          ok: false,
          error: "discord_rejected",
          discordStatus: discordResp.status
        }), {
          status: 502,
          headers: { "content-type": "application/json" }
        });
      }

      let githubDispatched = false;
      const ghToken = typeof env.GITHUB_TOKEN === "string" ? env.GITHUB_TOKEN.trim() : "";
      const ghRepo = typeof env.GITHUB_REPO === "string" && env.GITHUB_REPO.trim()
        ? env.GITHUB_REPO.trim()
        : "ibrahimk63/battery-widget";

      if (ghToken) {
        try {
          const ghResp = await fetch(`https://api.github.com/repos/${ghRepo}/dispatches`, {
            method: "POST",
            headers: {
              "authorization": `Bearer ${ghToken}`,
              "accept": "application/vnd.github+json",
              "content-type": "application/json",
              "user-agent": "BatteryWidget-Telemetry/1.2",
              "x-github-api-version": "2022-11-28"
            },
            body: JSON.stringify({
              event_type: "unknown_device_report",
              client_payload: {
                appVersion: appVer,
                detectedCount,
                candidates: normalizedCandidates
              }
            })
          });
          githubDispatched = ghResp.ok;
        } catch {
          githubDispatched = false;
        }
      }

      return new Response(JSON.stringify({
        ok: true,
        count: normalizedCandidates.length,
        delivered: true,
        githubDispatched
      }), {
        status: 200,
        headers: { "content-type": "application/json" }
      });
    } catch {
      return new Response("Bad Request", { status: 400 });
    }
  }
};
