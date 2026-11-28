export const GITHUB_MODELS_URL = "https://models.github.ai/inference/chat/completions";
export const DEFAULT_MODELS = ["openai/gpt-4o", "openai/gpt-4o-mini"];

export const VALID_CATEGORIES = new Set(["Mouse", "Keyboard", "Headset", "Gamepad", "Other"]);
export const VALID_CHECKSUMS = new Set(["None", "SumMod256", "Compx55", "XorCrc"]);

function normalizeHex4(value) {
  const cleaned = String(value ?? "")
    .trim()
    .replace(/^0x/i, "")
    .replace(/[^0-9a-f]/gi, "");
  if (!cleaned)
    return "0x0000";
  const num = Number.parseInt(cleaned, 16);
  if (!Number.isFinite(num) || num < 0)
    return "0x0000";
  return `0x${num.toString(16).toUpperCase().padStart(4, "0")}`;
}

function normalizeOptionalHexPacket(raw) {
  if (raw === null || raw === undefined)
    return null;
  const trimmed = String(raw).trim();
  if (!trimmed)
    return null;

  const tokens = trimmed
    .replace(/[-,]/g, " ")
    .split(/\s+/)
    .filter(Boolean);

  if (tokens.length === 0 || tokens.length > 64)
    return { valid: false, value: null };

  const normalized = [];
  for (const tok of tokens) {
    const clean = tok.replace(/^0x/i, "");
    if (!/^[0-9a-f]{1,2}$/i.test(clean))
      return { valid: false, value: null };
    normalized.push(clean.toUpperCase().padStart(2, "0"));
  }

  return { valid: true, value: normalized.join(" ") };
}

function isIntInRange(val, min, max) {
  return typeof val === "number" && Number.isInteger(val) && val >= min && val <= max;
}

function stripCodeFences(rawText) {
  const text = String(rawText ?? "").trim();
  const fenceMatch = /^```(?:json)?\s*\n?([\s\S]*?)\n?```$/i.exec(text);
  return fenceMatch ? fenceMatch[1].trim() : text;
}

/**
 * Validates AI-extracted profile strictly against Models/HidDeviceProfile.cs constraints
 * and ensures all cited URLs exist verbatim in allowedSourceUrls.
 */
export function validateExtractedProfile(rawResponse, candidate, allowedSourceUrls = []) {
  if (!rawResponse || typeof rawResponse !== "object") {
    return { accepted: false, isNonBattery: false, reason: "invalid_response_object" };
  }

  const status = String(rawResponse.status ?? "").trim();
  const confidence = String(rawResponse.confidence ?? "").trim().toLowerCase();
  const reason = String(rawResponse.reason ?? "").trim().slice(0, 500);
  const allowedSet = new Set(Array.isArray(allowedSourceUrls) ? allowedSourceUrls : []);
  const rawUrls = Array.isArray(rawResponse.sourceUrls) ? rawResponse.sourceUrls.map((u) => String(u ?? "").trim()) : [];
  const verifiedUrls = rawUrls.filter((u) => u && allowedSet.has(u));

  if (!["found", "non_battery", "insufficient_evidence"].includes(status)) {
    return { accepted: false, isNonBattery: false, reason: `invalid_status:${status}` };
  }

  if (status === "non_battery") {
    if (confidence === "high" && verifiedUrls.length > 0) {
      return {
        accepted: false,
        isNonBattery: true,
        confidence,
        reason: reason || "Confirmed non-battery hardware in OSS evidence",
        sourceUrls: verifiedUrls
      };
    }
    return {
      accepted: false,
      isNonBattery: false,
      reason: reason || "non_battery classification lacked high confidence or verified URLs"
    };
  }

  if (status !== "found") {
    return {
      accepted: false,
      isNonBattery: false,
      reason: reason || "insufficient_evidence"
    };
  }

  if (confidence !== "high" && confidence !== "medium") {
    return { accepted: false, isNonBattery: false, reason: `low_confidence:${confidence}` };
  }

  if (verifiedUrls.length === 0) {
    return { accepted: false, isNonBattery: false, reason: "no_verified_source_urls" };
  }

  const p = rawResponse.profile;
  if (!p || typeof p !== "object") {
    return { accepted: false, isNonBattery: false, reason: "missing_profile_object" };
  }

  const name = String(p.name ?? "").trim();
  if (!name || name.length > 80) {
    return { accepted: false, isNonBattery: false, reason: "invalid_profile_name" };
  }

  const category = String(p.category ?? "").trim();
  if (!VALID_CATEGORIES.has(category)) {
    return { accepted: false, isNonBattery: false, reason: `invalid_category:${category}` };
  }

  const candidateVid = normalizeHex4(candidate?.vid ?? candidate?.Vid);
  const candidatePid = normalizeHex4(candidate?.pid ?? candidate?.Pid);
  const profileVid = normalizeHex4(p.vendorIdHex);

  if (candidateVid === "0x0000" || profileVid !== candidateVid) {
    return { accepted: false, isNonBattery: false, reason: `vendor_id_mismatch:${profileVid}!=${candidateVid}` };
  }

  if (!Array.isArray(p.productIdsHex) || p.productIdsHex.length === 0) {
    return { accepted: false, isNonBattery: false, reason: "empty_productIdsHex" };
  }

  const productIdsHex = [...new Set(p.productIdsHex.map(normalizeHex4))].filter((x) => x !== "0x0000");
  if (!productIdsHex.includes(candidatePid)) {
    return { accepted: false, isNonBattery: false, reason: `missing_candidate_pid:${candidatePid}` };
  }

  const wiredProductIdsHex = Array.isArray(p.wiredProductIdsHex)
    ? [...new Set(p.wiredProductIdsHex.map(normalizeHex4))].filter((x) => x !== "0x0000")
    : undefined;

  let usagePageHex = null;
  if (p.usagePageHex !== undefined && p.usagePageHex !== null && String(p.usagePageHex).trim() !== "") {
    usagePageHex = normalizeHex4(p.usagePageHex);
    if (usagePageHex === "0x0000") {
      return { accepted: false, isNonBattery: false, reason: "invalid_usagePageHex" };
    }
  }

  const parsedQuery = normalizeOptionalHexPacket(p.queryPacketHex);
  if (parsedQuery && !parsedQuery.valid) {
    return { accepted: false, isNonBattery: false, reason: "invalid_queryPacketHex" };
  }
  const queryPacketHex = parsedQuery?.value ?? null;

  const parsedWakeup = normalizeOptionalHexPacket(p.wakeupPacketHex);
  if (parsedWakeup && !parsedWakeup.valid) {
    return { accepted: false, isNonBattery: false, reason: "invalid_wakeupPacketHex" };
  }
  const wakeupPacketHex = parsedWakeup?.value ?? null;

  const useFeatureReport = Boolean(p.useFeatureReport);
  const readPassiveInput = Boolean(p.readPassiveInput);
  const mergeProductIds = Boolean(p.mergeProductIds);

  if (!readPassiveInput && !queryPacketHex) {
    return { accepted: false, isNonBattery: false, reason: "must_set_readPassiveInput_or_queryPacketHex" };
  }

  const expectedReportId = p.expectedReportId ?? -1;
  if (!isIntInRange(expectedReportId, -1, 255)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_expectedReportId" };
  }

  const batteryByteIndex = p.batteryByteIndex;
  if (!isIntInRange(batteryByteIndex, 0, 127)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_batteryByteIndex" };
  }

  const batteryMask = p.batteryMask ?? 255;
  if (!isIntInRange(batteryMask, 1, 255)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_batteryMask" };
  }

  const maxBatteryValue = p.maxBatteryValue ?? 100;
  if (!isIntInRange(maxBatteryValue, 1, 255)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_maxBatteryValue" };
  }

  const chargingByteIndex = p.chargingByteIndex ?? null;
  if (chargingByteIndex !== null && !isIntInRange(chargingByteIndex, 0, 127)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_chargingByteIndex" };
  }

  const chargingMask = p.chargingMask ?? 1;
  if (!isIntInRange(chargingMask, 0, 255)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_chargingMask" };
  }

  const chargingEqualsValue = p.chargingEqualsValue ?? null;
  if (chargingEqualsValue !== null && !isIntInRange(chargingEqualsValue, 0, 255)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_chargingEqualsValue" };
  }

  const linkStatusByteIndex = p.linkStatusByteIndex ?? null;
  if (linkStatusByteIndex !== null && !isIntInRange(linkStatusByteIndex, 0, 127)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_linkStatusByteIndex" };
  }

  const linkStatusOnlineValue = p.linkStatusOnlineValue ?? 1;
  if (!isIntInRange(linkStatusOnlineValue, 0, 255)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_linkStatusOnlineValue" };
  }

  const passiveStaleSeconds = p.passiveStaleSeconds ?? 25;
  if (!isIntInRange(passiveStaleSeconds, 5, 300)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_passiveStaleSeconds" };
  }

  const checksumMode = p.checksumMode ?? "None";
  if (!VALID_CHECKSUMS.has(checksumMode)) {
    return { accepted: false, isNonBattery: false, reason: `invalid_checksumMode:${checksumMode}` };
  }

  const checksumByteIndex = p.checksumByteIndex ?? -1;
  if (!isIntInRange(checksumByteIndex, -1, 127)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_checksumByteIndex" };
  }

  const voltageHighByteIndex = p.voltageHighByteIndex ?? null;
  if (voltageHighByteIndex !== null && !isIntInRange(voltageHighByteIndex, 0, 127)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_voltageHighByteIndex" };
  }

  const voltageLowByteIndex = p.voltageLowByteIndex ?? null;
  if (voltageLowByteIndex !== null && !isIntInRange(voltageLowByteIndex, 0, 127)) {
    return { accepted: false, isNonBattery: false, reason: "invalid_voltageLowByteIndex" };
  }

  // Construct sanitized profile with only known HidDeviceProfile keys
  const sanitized = {
    name,
    category,
    vendorIdHex: profileVid,
    productIdsHex
  };

  if (wiredProductIdsHex && wiredProductIdsHex.length > 0) sanitized.wiredProductIdsHex = wiredProductIdsHex;
  if (usagePageHex) sanitized.usagePageHex = usagePageHex;
  if (wakeupPacketHex) sanitized.wakeupPacketHex = wakeupPacketHex;
  if (queryPacketHex) sanitized.queryPacketHex = queryPacketHex;
  sanitized.useFeatureReport = useFeatureReport;
  sanitized.readPassiveInput = readPassiveInput;
  if (mergeProductIds) sanitized.mergeProductIds = true;
  sanitized.expectedReportId = expectedReportId;
  if (linkStatusByteIndex !== null) {
    sanitized.linkStatusByteIndex = linkStatusByteIndex;
    sanitized.linkStatusOnlineValue = linkStatusOnlineValue;
  }
  sanitized.batteryByteIndex = batteryByteIndex;
  sanitized.batteryMask = batteryMask;
  if (chargingByteIndex !== null) {
    sanitized.chargingByteIndex = chargingByteIndex;
    if (chargingEqualsValue !== null) {
      sanitized.chargingEqualsValue = chargingEqualsValue;
    } else {
      sanitized.chargingMask = chargingMask;
    }
  }
  if (readPassiveInput) sanitized.passiveStaleSeconds = passiveStaleSeconds;
  if (checksumMode !== "None") {
    sanitized.checksumMode = checksumMode;
    sanitized.checksumByteIndex = checksumByteIndex;
  }
  if (voltageHighByteIndex !== null && voltageLowByteIndex !== null) {
    sanitized.voltageHighByteIndex = voltageHighByteIndex;
    sanitized.voltageLowByteIndex = voltageLowByteIndex;
  }
  sanitized.maxBatteryValue = maxBatteryValue;

  return {
    accepted: true,
    isNonBattery: false,
    confidence,
    reason,
    sourceUrls: verifiedUrls,
    profile: sanitized
  };
}

function buildPrompts(candidate, evidence) {
  const systemPrompt = [
    "You are an expert USB HID firmware and battery protocol analyst for BatteryWidget (Windows C#/.NET).",
    "Analyze ONLY the provided open-source evidence snippets for the target USB HID VID:PID.",
    "STRICT RULES:",
    "1. NEVER guess or invent packet bytes, byte indices, checksums, or URLs.",
    "2. If the evidence does not clearly show BOTH how to read battery (passive report OR query command) AND which byte index holds battery percentage, set status to \"insufficient_evidence\".",
    "3. If the evidence clearly proves the device is NOT a rechargeable wireless battery device (e.g., I2C laptop touchpad, virtual driver, RGB controller, purely wired mouse with no battery), set status to \"non_battery\".",
    "4. Every URL in \"sourceUrls\" MUST appear verbatim in the Evidence section.",
    "5. Output ONLY a single JSON object matching this exact schema:",
    "{",
    '  "status": "found" | "non_battery" | "insufficient_evidence",',
    '  "confidence": "high" | "medium" | "low",',
    '  "reason": "short explanation citing byte offsets and report IDs",',
    '  "sourceUrls": ["https://..."],',
    '  "profile": {',
    '    "name": "Device Name",',
    '    "category": "Mouse" | "Keyboard" | "Headset" | "Gamepad" | "Other",',
    '    "vendorIdHex": "0xXXXX",',
    '    "productIdsHex": ["0xYYYY"],',
    '    "wiredProductIdsHex": [],',
    '    "usagePageHex": null,',
    '    "wakeupPacketHex": null,',
    '    "queryPacketHex": "08 03" | null,',
    '    "useFeatureReport": false,',
    '    "readPassiveInput": false,',
    '    "mergeProductIds": false,',
    '    "expectedReportId": -1,',
    '    "linkStatusByteIndex": null,',
    '    "linkStatusOnlineValue": 1,',
    '    "batteryByteIndex": 1,',
    '    "batteryMask": 255,',
    '    "chargingByteIndex": null,',
    '    "chargingMask": 1,',
    '    "chargingEqualsValue": null,',
    '    "passiveStaleSeconds": 25,',
    '    "checksumMode": "None" | "SumMod256" | "Compx55" | "XorCrc",',
    '    "checksumByteIndex": -1,',
    '    "maxBatteryValue": 100',
    "  }",
    "}"
  ].join("\n");

  const evidenceBlocks = evidence
    .map((e, i) => `### Evidence [${i + 1}]\nSource: ${e.source}\nURL: ${e.url}\nTitle: ${e.title}\nContent:\n${e.text}`)
    .join("\n\n");

  const userPrompt = [
    "Target Unknown Hardware:",
    `- Product Name: ${candidate.productName ?? "Unknown"}`,
    `- VID:PID: ${candidate.vid}:${candidate.pid}`,
    `- HID Report Lengths: MaxIn=${candidate.maxIn ?? 0}, MaxOut=${candidate.maxOut ?? 0}, MaxFeat=${candidate.maxFeat ?? 0}`,
    `- USB IDs Vendor: ${candidate.usbVendorName ?? "Unknown"}`,
    `- USB IDs Device: ${candidate.usbDeviceName ?? "Unknown"}`,
    "",
    "Open-Source Evidence:",
    evidenceBlocks
  ].join("\n");

  return { systemPrompt, userPrompt };
}

/**
 * Calls free GitHub Models inference API using GITHUB_TOKEN (`models: read`)
 * to extract a strictly grounded HidDeviceProfile proposal from OSS evidence.
 */
export async function extractProtocolWithAi(candidate, evidence, options = {}) {
  const token = options.token ?? "";
  if (!evidence || evidence.length === 0 || !String(token).trim()) {
    return {
      accepted: false,
      isNonBattery: false,
      status: "skipped",
      reason: "no_evidence_or_token",
      sourceUrls: []
    };
  }

  const fetchImpl = options.fetchImpl ?? fetch;
  const models = Array.isArray(options.models) && options.models.length > 0 ? options.models : DEFAULT_MODELS;
  const endpointUrl = options.endpointUrl ?? GITHUB_MODELS_URL;
  const timeoutMs = Number.isFinite(options.timeoutMs) ? options.timeoutMs : 20000;
  const allowedUrls = evidence.map((e) => e.url).filter(Boolean);
  const { systemPrompt, userPrompt } = buildPrompts(candidate, evidence);

  let lastError = "all_models_failed";

  for (const model of models) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const resp = await fetchImpl(endpointUrl, {
        method: "POST",
        signal: controller.signal,
        headers: {
          "Content-Type": "application/json",
          "Authorization": `Bearer ${String(token).trim()}`
        },
        body: JSON.stringify({
          model,
          messages: [
            { role: "system", content: systemPrompt },
            { role: "user", content: userPrompt }
          ],
          temperature: 0.1,
          max_tokens: 900,
          response_format: { type: "json_object" }
        })
      });

      if (!resp.ok) {
        lastError = `http_${resp.status}_on_${model}`;
        // Retry next fallback model on rate-limit, model unavailability, or transient server errors
        if (resp.status === 429 || resp.status === 404 || resp.status >= 500) {
          continue;
        }
        return {
          accepted: false,
          isNonBattery: false,
          status: "error",
          model,
          reason: lastError,
          sourceUrls: []
        };
      }

      const data = await resp.json();
      const content = data?.choices?.[0]?.message?.content;
      if (!content) {
        lastError = `empty_content_on_${model}`;
        continue;
      }

      let parsed;
      try {
        parsed = JSON.parse(stripCodeFences(content));
      } catch {
        lastError = `invalid_json_on_${model}`;
        continue;
      }

      const validated = validateExtractedProfile(parsed, candidate, allowedUrls);
      return {
        ...validated,
        status: parsed.status ?? "unknown",
        model
      };
    } catch (err) {
      lastError = `fetch_error_on_${model}:${err?.message ?? "unknown"}`;
      continue;
    } finally {
      clearTimeout(timer);
    }
  }

  return {
    accepted: false,
    isNonBattery: false,
    status: "error",
    reason: lastError,
    sourceUrls: []
  };
}
