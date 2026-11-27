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

      const lines = candidates.map((c) => {
        const vid = String(c.Vid || c.vid || "0x0000").slice(0, 8);
        const pid = String(c.Pid || c.pid || "0x0000").slice(0, 8);
        const name = String(c.ProductName || c.productName || "Unknown").slice(0, 64);
        const inLen = Number(c.MaxIn ?? c.maxIn ?? 0);
        const outLen = Number(c.MaxOut ?? c.maxOut ?? 0);
        const featLen = Number(c.MaxFeat ?? c.maxFeat ?? 0);
        return `• **${name}** — \`${vid}:${pid}\` (In:${inLen} Out:${outLen} Feat:${featLen})`;
      });

      const webhookUrl = typeof env.DISCORD_WEBHOOK_URL === "string" ? env.DISCORD_WEBHOOK_URL.trim() : "";
      if (!webhookUrl) {
        return new Response(JSON.stringify({ ok: false, error: "webhook_not_configured" }), {
          status: 503,
          headers: { "content-type": "application/json" }
        });
      }

      const discordResp = await fetch(webhookUrl, {
        method: "POST",
        headers: {
          "content-type": "application/json",
          "user-agent": "BatteryWidget-Telemetry/1.1"
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

      return new Response(JSON.stringify({ ok: true, count: candidates.length, delivered: true }), {
        status: 200,
        headers: { "content-type": "application/json" }
      });
    } catch {
      return new Response("Bad Request", { status: 400 });
    }
  }
};
