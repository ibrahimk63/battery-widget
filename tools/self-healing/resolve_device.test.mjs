import test from "node:test";
import assert from "node:assert/strict";
import {
  inferCategory,
  isCoveredInDatabase,
  isNonBatteryNoise,
  matchKnownFamily,
  normalizeHex,
  parseIssuesToCandidates,
  parseUsbIds,
  resolveCandidates,
  resolveCandidatesAsync
} from "./resolve_device.mjs";
import { parseLibratbagDeviceIni, searchOpenSourceEvidence } from "./oss_research.mjs";
import { extractProtocolWithAi, validateExtractedProfile } from "./ai_research.mjs";

const sampleDb = {
  schemaVersion: 1,
  updatedAt: "2026-11-27T16:05:00Z",
  reportEndpointUrl: "https://battery-widget.ibrqhimklc.workers.dev",
  profiles: [
    {
      name: "Attack Shark R5 Ultra",
      category: "Mouse",
      vendorIdHex: "0x373E",
      productIdsHex: ["0x0047"]
    },
    {
      name: "Pulsar Wildcard",
      category: "Mouse",
      vendorIdHex: "0x3710",
      productIdsHex: []
    }
  ]
};

test("normalizeHex formats VID/PID consistently", () => {
  assert.equal(normalizeHex("0x373e"), "0x373E");
  assert.equal(normalizeHex("47"), "0x0047");
  assert.equal(normalizeHex(""), "0x0000");
});

test("inferCategory distinguishes Headset, Keyboard, Gamepad, and Mouse", () => {
  assert.equal(inferCategory("HyperX Cloud III Wireless"), "Headset");
  assert.equal(inferCategory("Keychron K2 Pro Keyboard"), "Keyboard");
  assert.equal(inferCategory("Sony DualSense Controller"), "Gamepad");
  assert.equal(inferCategory("ATK Blazing Sky F1 Mouse"), "Mouse");
});

test("isCoveredInDatabase checks exact PID and wildcard empty list", () => {
  assert.equal(isCoveredInDatabase(sampleDb, "0x373E", "0x0047"), true);
  assert.equal(isCoveredInDatabase(sampleDb, "0x373E", "0x0099"), false);
  assert.equal(isCoveredInDatabase(sampleDb, "0x3710", "0x1234"), true);
});

test("resolveCandidates resolves verified CompX OEM and keeps unknown vendors unresolved", () => {
  const result = resolveCandidates(
    sampleDb,
    [
      { vid: "0x373E", pid: "0x0047", productName: "Attack Shark R5 Ultra", maxIn: 64, maxOut: 64, maxFeat: 0 },
      { vid: "0x3554", pid: "0xFA12", productName: "ATK Blazing Sky F1", maxIn: 64, maxOut: 64, maxFeat: 0 },
      { vid: "0x9999", pid: "0x1234", productName: "Mystery Brand Device", maxIn: 64, maxOut: 64, maxFeat: 0 }
    ],
    {
      nowIso: "2026-11-27T20:00:00Z",
      usbIdsText: "9999  Mystery Corp\n\t1234  Secret Mouse\n"
    }
  );

  assert.equal(result.alreadyCovered.length, 1);
  assert.equal(result.resolved.length, 1);
  assert.equal(result.unresolved.length, 1);
  assert.equal(result.dbUpdated, true);
  assert.equal(result.resolved[0].family, "CompX/PAW3950");
  assert.equal(result.resolved[0].profile.checksumMode, "Compx55");
  assert.equal(result.db.updatedAt, "2026-11-27T20:00:00Z");

  assert.equal(result.unresolved[0].usbVendorName, "Mystery Corp");
  assert.equal(result.unresolved[0].usbDeviceName, "Secret Mouse");
});

test("matchKnownFamily skips CompX VID with too-short report descriptors", () => {
  const match = matchKnownFamily({
    vid: "0x3554",
    pid: "0xFA12",
    productName: "Boot Interface Only",
    maxIn: 8,
    maxOut: 8,
    maxFeat: 0
  });
  assert.equal(match, null);
});

test("parseUsbIds extracts vendor and device safely", () => {
  const text = [
    "# comment",
    "046d  Logitech, Inc.",
    "\tc547  Lightspeed Receiver",
    "1532  Razer USA, Ltd",
    "\t00aa  Viper V2 Pro"
  ].join("\n");

  assert.deepEqual(parseUsbIds(text, "0x046D", "0xC547"), {
    vendorName: "Logitech, Inc.",
    deviceName: "Lightspeed Receiver"
  });
  assert.deepEqual(parseUsbIds(text, "0x046D", "0x9999"), {
    vendorName: "Logitech, Inc.",
    deviceName: null
  });
});

test("isNonBatteryNoise filters virtual multitouch and ITE RGB devices", () => {
  assert.equal(isNonBatteryNoise({ vid: "0x00FF", pid: "0xBACC", productName: "Virtual Multitouch Device" }), true);
  assert.equal(isNonBatteryNoise({ vid: "0x048D", pid: "0x5702", productName: "ITE Device" }), true);
  assert.equal(isNonBatteryNoise({ vid: "0x04F3", pid: "0x026F", productName: "HighSpeed Receiver" }), false);
});

test("resolveCandidates ignores system noise and resolves Elan/PAW3950 passive-push family", () => {
  const result = resolveCandidates(sampleDb, [
    { vid: "0x00FF", pid: "0xBACC", productName: "Virtual Multitouch Device", maxIn: 65, maxOut: 65, maxFeat: 0 },
    { vid: "0x048D", pid: "0x5702", productName: "ITE Device", maxIn: 0, maxOut: 0, maxFeat: 64 },
    { vid: "0x04F3", pid: "0x026F", productName: "HighSpeed Receiver", maxIn: 0, maxOut: 0, maxFeat: 136 }
  ]);

  assert.equal(result.ignored.length, 2);
  assert.equal(result.unresolved.length, 0);
  assert.equal(result.resolved.length, 1);
  assert.equal(result.resolved[0].family, "Elan/PAW3950-Passive");
  assert.equal(result.resolved[0].profile.name, "Throne Repel 8K");
  assert.equal(result.resolved[0].profile.readPassiveInput, true);
  assert.equal(result.resolved[0].profile.expectedReportId, 4);
  assert.equal(result.resolved[0].profile.linkStatusByteIndex, 1);
  assert.deepEqual(result.resolved[0].profile.productIdsHex, ["0x026F", "0x026E"]);
});

test("isNonBatteryNoise filters HIDI2C touchpads and wired-only 0x30FA mice while keeping wireless", () => {
  assert.equal(isNonBatteryNoise({ vid: "0x093A", pid: "0x0255", productName: "HIDI2C Device" }), true);
  assert.equal(isNonBatteryNoise({ vid: "0x04F3", pid: "0x3158", productName: "HIDI2C Device" }), true);
  assert.equal(isNonBatteryNoise({ vid: "0x30FA", pid: "0x1440", productName: "USB GAMING MOUSE" }), true);
  assert.equal(isNonBatteryNoise({ vid: "0x30FA", pid: "0x1441", productName: "2.4G Wireless Gaming Mouse" }), false);
});

test("parseIssuesToCandidates extracts VID, PID, name, and report lengths from issue markdown", () => {
  const parsed = parseIssuesToCandidates([
    {
      number: 12,
      title: "[Unknown Device] MCHOSE K99 V3 (0x1234:0xABCD)",
      body: "## Anonim Cihaz Raporu\n\n- **Ürün Adı:** MCHOSE K99 V3\n- **VID:PID:** `0x1234:0xABCD`\n- **HID Rapor Boyutları:** `In:64 Out:32 Feat:8`"
    }
  ]);

  assert.equal(parsed.length, 1);
  assert.deepEqual(parsed[0], {
    vid: "0x1234",
    pid: "0xABCD",
    productName: "MCHOSE K99 V3",
    maxIn: 64,
    maxOut: 32,
    maxFeat: 8,
    issueNumber: 12
  });
});

test("parseLibratbagDeviceIni extracts matching USB device name and driver", () => {
  const ini = [
    "[Device]",
    "Name=Logitech G Pro X Superlight",
    "DeviceMatch=usb:046d:c547;usb:046d:4093",
    "Driver=hidpp20"
  ].join("\n");

  assert.deepEqual(parseLibratbagDeviceIni(ini, "0x046D", "0xC547"), {
    name: "Logitech G Pro X Superlight",
    driver: "hidpp20"
  });
  assert.equal(parseLibratbagDeviceIni(ini, "0x046D", "0x9999"), null);
});

test("searchOpenSourceEvidence gathers issue + comments and degrades gracefully on 403 code search", async () => {
  const calls = [];
  const mockFetch = async (url) => {
    calls.push(url);
    if (url.includes("/search/issues") && url.includes("1234%3Aabcd")) {
      return {
        ok: true,
        status: 200,
        json: async () => ({
          items: [
            {
              html_url: "https://github.com/example/repo/issues/42",
              repository_url: "https://api.github.com/repos/example/repo",
              title: "Support Foo Mouse 1234:abcd",
              body: "Needs report 0x05 query",
              comments: 1,
              comments_url: "https://api.github.com/repos/example/repo/issues/42/comments"
            }
          ]
        })
      };
    }
    if (url.includes("/issues/42/comments")) {
      return {
        ok: true,
        status: 200,
        json: async () => [{ body: "Battery is byte 4, charging byte 5" }]
      };
    }
    if (url.includes("/search/issues")) {
      return { ok: true, status: 200, json: async () => ({ items: [] }) };
    }
    if (url.includes("/search/code")) {
      return { ok: false, status: 403, json: async () => ({ message: "Forbidden" }) };
    }
    return { ok: false, status: 404, json: async () => ({}) };
  };

  const res = await searchOpenSourceEvidence(
    { vid: "0x1234", pid: "0xABCD" },
    { token: "ghp_test", fetchImpl: mockFetch }
  );

  assert.equal(res.evidence.length, 1);
  assert.equal(res.evidence[0].url, "https://github.com/example/repo/issues/42");
  assert.match(res.evidence[0].text, /Battery is byte 4/);
  assert.ok(res.searchErrors.includes("code_search:403"));
  assert.ok(calls.some((u) => u.includes("/search/code")));
});

test("validateExtractedProfile enforces strict schema and URL grounding", () => {
  const candidate = { vid: "0x1234", pid: "0xABCD" };
  const allowed = ["https://github.com/example/repo/issues/42"];

  // Valid passive profile
  const validPassive = validateExtractedProfile({
    status: "found",
    confidence: "high",
    reason: "Report 0x04 pushes battery at byte 4",
    sourceUrls: allowed,
    profile: {
      name: "Foo Wireless Mouse",
      category: "Mouse",
      vendorIdHex: "0x1234",
      productIdsHex: ["0xABCD"],
      readPassiveInput: true,
      expectedReportId: 4,
      batteryByteIndex: 4,
      chargingByteIndex: 5,
      chargingEqualsValue: 1,
      extraUnsafeField: "should_be_stripped"
    }
  }, candidate, allowed);

  assert.equal(validPassive.accepted, true);
  assert.equal(validPassive.profile.readPassiveInput, true);
  assert.equal("extraUnsafeField" in validPassive.profile, false);

  // Valid active query profile
  const validActive = validateExtractedProfile({
    status: "found",
    confidence: "medium",
    reason: "Query 08 03",
    sourceUrls: allowed,
    profile: {
      name: "Foo Query Mouse",
      category: "Mouse",
      vendorIdHex: "0x1234",
      productIdsHex: ["0xABCD"],
      queryPacketHex: "08 03",
      readPassiveInput: false,
      expectedReportId: 8,
      batteryByteIndex: 6,
      checksumMode: "Compx55",
      checksumByteIndex: 16
    }
  }, candidate, allowed);
  assert.equal(validActive.accepted, true);

  // Reject hallucinated URL
  const badUrl = validateExtractedProfile({
    status: "found",
    confidence: "high",
    reason: "test",
    sourceUrls: ["https://github.com/fake/hallucinated/issues/1"],
    profile: {
      name: "Foo",
      category: "Mouse",
      vendorIdHex: "0x1234",
      productIdsHex: ["0xABCD"],
      readPassiveInput: true,
      batteryByteIndex: 4
    }
  }, candidate, allowed);
  assert.equal(badUrl.accepted, false);
  assert.equal(badUrl.reason, "no_verified_source_urls");

  // Reject mismatched VID
  const badVid = validateExtractedProfile({
    status: "found",
    confidence: "high",
    reason: "test",
    sourceUrls: allowed,
    profile: {
      name: "Foo",
      category: "Mouse",
      vendorIdHex: "0x9999",
      productIdsHex: ["0xABCD"],
      readPassiveInput: true,
      batteryByteIndex: 4
    }
  }, candidate, allowed);
  assert.equal(badVid.accepted, false);

  // Reject invalid checksumMode & missing passive+query
  const badMode = validateExtractedProfile({
    status: "found",
    confidence: "high",
    reason: "test",
    sourceUrls: allowed,
    profile: {
      name: "Foo",
      category: "Mouse",
      vendorIdHex: "0x1234",
      productIdsHex: ["0xABCD"],
      readPassiveInput: false,
      queryPacketHex: "",
      batteryByteIndex: 4
    }
  }, candidate, allowed);
  assert.equal(badMode.accepted, false);

  const badChecksum = validateExtractedProfile({
    status: "found",
    confidence: "high",
    reason: "test",
    sourceUrls: allowed,
    profile: {
      name: "Foo",
      category: "Mouse",
      vendorIdHex: "0x1234",
      productIdsHex: ["0xABCD"],
      readPassiveInput: true,
      checksumMode: "BogusAlgo",
      batteryByteIndex: 4
    }
  }, candidate, allowed);
  assert.equal(badChecksum.accepted, false);

  // High confidence non_battery
  const nonBat = validateExtractedProfile({
    status: "non_battery",
    confidence: "high",
    reason: "Wired RGB hub only",
    sourceUrls: allowed
  }, candidate, allowed);
  assert.equal(nonBat.accepted, false);
  assert.equal(nonBat.isNonBattery, true);
});

test("extractProtocolWithAi falls back to gpt-4o-mini on 429 rate limit", async () => {
  const modelsTried = [];
  const mockFetch = async (_url, init) => {
    const body = JSON.parse(init.body);
    modelsTried.push(body.model);
    if (body.model === "openai/gpt-4o") {
      return { ok: false, status: 429, json: async () => ({}) };
    }
    return {
      ok: true,
      status: 200,
      json: async () => ({
        choices: [
          {
            message: {
              content: JSON.stringify({
                status: "found",
                confidence: "high",
                reason: "Passive report 0x05 byte 3",
                sourceUrls: ["https://github.com/example/repo/issues/9"],
                profile: {
                  name: "Fallback Wireless Mouse",
                  category: "Mouse",
                  vendorIdHex: "0x1111",
                  productIdsHex: ["0x2222"],
                  readPassiveInput: true,
                  expectedReportId: 5,
                  batteryByteIndex: 3
                }
              })
            }
          }
        ]
      })
    };
  };

  const res = await extractProtocolWithAi(
    { vid: "0x1111", pid: "0x2222", productName: "Fallback Wireless Mouse" },
    [{ source: "github-issue", title: "Mouse", url: "https://github.com/example/repo/issues/9", text: "report 5 byte 3" }],
    { token: "ghp_test", fetchImpl: mockFetch }
  );

  assert.deepEqual(modelsTried, ["openai/gpt-4o", "openai/gpt-4o-mini"]);
  assert.equal(res.accepted, true);
  assert.equal(res.model, "openai/gpt-4o-mini");
  assert.equal(res.profile.batteryByteIndex, 3);
});

test("resolveCandidatesAsync integrates Layer 1 noise, Layer 2 CompX, Layer 3+4 AI PR proposal, and unresolved", async () => {
  const mockFetch = async (url, init) => {
    if (url.includes("models.github.ai")) {
      const payload = JSON.parse(init.body);
      const userContent = payload.messages[1].content;
      if (userContent.includes("0x1111:0x2222")) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            choices: [
              {
                message: {
                  content: JSON.stringify({
                    status: "found",
                    confidence: "high",
                    reason: "Verified query 0xAA byte 2",
                    sourceUrls: ["https://github.com/oss/mouse/issues/1"],
                    profile: {
                      name: "OSS Discovered Mouse",
                      category: "Mouse",
                      vendorIdHex: "0x1111",
                      productIdsHex: ["0x2222"],
                      queryPacketHex: "AA 01",
                      readPassiveInput: false,
                      expectedReportId: 170,
                      batteryByteIndex: 2
                    }
                  })
                }
              }
            ]
          })
        };
      }
    }
    if (url.includes("/search/issues") && url.includes("1111")) {
      return {
        ok: true,
        status: 200,
        json: async () => ({
          items: [
            {
              html_url: "https://github.com/oss/mouse/issues/1",
              repository_url: "https://api.github.com/repos/oss/mouse",
              title: "Protocol 1111:2222",
              body: "Send AA 01, battery at byte 2",
              comments: 0
            }
          ]
        })
      };
    }
    return { ok: true, status: 200, json: async () => ({ items: [] }) };
  };

  const result = await resolveCandidatesAsync(
    sampleDb,
    [
      { vid: "0x093A", pid: "0x0255", productName: "HIDI2C Device", maxIn: 0, maxOut: 0, maxFeat: 64 },
      { vid: "0x3554", pid: "0xFA12", productName: "ATK Blazing Sky F1", maxIn: 64, maxOut: 64, maxFeat: 0 },
      { vid: "0x1111", pid: "0x2222", productName: "OSS Wireless Mouse", maxIn: 64, maxOut: 64, maxFeat: 0 },
      { vid: "0x8888", pid: "0x9999", productName: "Truly Unknown Mouse", maxIn: 64, maxOut: 64, maxFeat: 0 }
    ],
    {
      enableAi: true,
      githubToken: "ghp_mock",
      fetchImpl: mockFetch,
      nowIso: "2026-11-28T09:00:00Z"
    }
  );

  assert.equal(result.ignored.length, 1);
  assert.equal(result.resolved.length, 1);
  assert.equal(result.aiProposals.length, 1);
  assert.equal(result.unresolved.length, 1);
  assert.equal(result.dbUpdated, true);
  // Layer 4 AI proposal must NOT be written into main db directly
  assert.equal(result.db.profiles.some((p) => p.vendorIdHex === "0x1111"), false);
  assert.equal(result.aiProposals[0].profile.name, "OSS Discovered Mouse");
  assert.equal(result.aiProposals[0].updatedDb.profiles.some((p) => p.vendorIdHex === "0x1111"), true);
  assert.equal(result.unresolved[0].vid, "0x8888");
});
