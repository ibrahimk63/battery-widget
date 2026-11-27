import test from "node:test";
import assert from "node:assert/strict";
import {
  inferCategory,
  isCoveredInDatabase,
  isNonBatteryNoise,
  matchKnownFamily,
  normalizeHex,
  parseUsbIds,
  resolveCandidates
} from "./resolve_device.mjs";

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
