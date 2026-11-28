import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { searchOpenSourceEvidence } from "./oss_research.mjs";
import { extractProtocolWithAi } from "./ai_research.mjs";

const USB_IDS_URL = "https://raw.githubusercontent.com/systemd/systemd/main/hwdb.d/usb.ids";

// VerifiedCompX / Nordic / PAW3950 OEM USB VIDs whose wireless receivers use Report 0x08 0x03 + Compx55 checksum.
const COMPX_VERIFIED_VIDS = new Set([
  "0x25A7", // Areson / CompX (Scyrox, Pulsar)
  "0x3554", // CompX / Nordic OEM (ATK, VXE, Mad R, Lamzu, Darmoshark variants)
  "0x3710", // Pulsar Wireless
  "0x373B", // ATK / VGN CompX OEM
  "0x373E", // Attack Shark / CompX OEM
  "0x37B0", // Wraith / OEM CompX
  "0x1D57"  // CompX 2.4GHz Wireless Dongle
]);

const NON_BATTERY_SYSTEM_VIDS = new Set([
  "0x00FF", // Virtual HID / Virtual Multitouch drivers
  "0x048D"  // ITE Tech motherboard/laptop EC & RGB lighting controllers
]);

export function isNonBatteryNoise(candidate) {
  const vid = normalizeHex(candidate.vid ?? candidate.Vid);
  const productName = String(candidate.productName ?? candidate.ProductName ?? "");

  if (NON_BATTERY_SYSTEM_VIDS.has(vid))
    return true;

  // Low-cost wired-only USB optical mouse controllers (e.g. Instant Microelectronics 0x30FA)
  if (vid === "0x30FA" && !/wireless|2\.4g|receiver|dongle|\bbt\b|bluetooth/i.test(productName))
    return true;

  return /virtual multitouch|virtual hid|ite device|mystic light|aura led|\bhidi2c\b|i2c hid|precision touchpad|elan touchpad|synaptics|asus aura|gigabyte rgb|nzxt hue|lighting node/i.test(productName);
}

/**
 * Parses open GitHub `[Unknown Device]` issues back into normalized resolver candidates
 * for scheduled weekly OSS/AI re-scanning.
 */
export function parseIssuesToCandidates(issues) {
  const candidates = [];
  for (const issue of issues ?? []) {
    const title = String(issue?.title ?? "");
    const body = String(issue?.body ?? "");

    const vidPidMatch =
      /\((0x[0-9a-f]{4}):(0x[0-9a-f]{4})\)/i.exec(title) ??
      /`(0x[0-9a-f]{4}):(0x[0-9a-f]{4})`/i.exec(body);
    if (!vidPidMatch)
      continue;

    const vid = normalizeHex(vidPidMatch[1]);
    const pid = normalizeHex(vidPidMatch[2]);

    const bodyNameMatch = /-\s*\*\*Ürün Adı:\*\*\s*(.+)/i.exec(body);
    const titleNameMatch = /\[Unknown Device\]\s*(.+?)\s*\(0x[0-9a-f]{4}:0x[0-9a-f]{4}\)/i.exec(title);
    const productName = (bodyNameMatch?.[1] ?? titleNameMatch?.[1] ?? "Unknown").trim();

    const lenMatch = /In:(\d+)\s+Out:(\d+)\s+Feat:(\d+)/i.exec(body);
    const maxIn = lenMatch ? Number(lenMatch[1]) : 0;
    const maxOut = lenMatch ? Number(lenMatch[2]) : 0;
    const maxFeat = lenMatch ? Number(lenMatch[3]) : 0;

    candidates.push({
      vid,
      pid,
      productName,
      maxIn,
      maxOut,
      maxFeat,
      issueNumber: issue.number ?? null
    });
  }
  return candidates;
}

export function normalizeHex(value) {
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

export function inferCategory(productName) {
  const name = String(productName ?? "").toLowerCase();
  if (/headset|headphone|audio|cloud|arctis|kraken|barracuda|blackshark|hs\d|kulakl/i.test(name))
    return "Headset";
  if (/keyboard|keychron|k\d|gmmk|wooting|klavye/i.test(name))
    return "Keyboard";
  if (/controller|gamepad|dualsense|dualshock|xbox|8bitdo/i.test(name))
    return "Gamepad";
  if (/mouse|r1|r5|m3|x2|xlite|superlight|viper|deathadder|pulsar|lamzu|scyrox|atk|vxe|mad r|ajazz|zaopin|fare/i.test(name))
    return "Mouse";
  return "Other";
}

export function isCoveredInDatabase(db, vidHex, pidHex) {
  const targetVid = normalizeHex(vidHex);
  const targetPid = normalizeHex(pidHex);
  const profiles = Array.isArray(db?.profiles) ? db.profiles : [];

  for (const profile of profiles) {
    if (normalizeHex(profile.vendorIdHex) !== targetVid)
      continue;

    const pids = Array.isArray(profile.productIdsHex) ? profile.productIdsHex : [];
    if (pids.length === 0)
      return true;

    if (pids.some((p) => normalizeHex(p) === targetPid))
      return true;
  }

  return false;
}

export function matchKnownFamily(candidate) {
  const vid = normalizeHex(candidate.vid ?? candidate.Vid);
  const pid = normalizeHex(candidate.pid ?? candidate.Pid);
  const rawName = String(candidate.productName ?? candidate.ProductName ?? "").trim();
  const productName = rawName && !/^bilinmeyen/i.test(rawName) ? rawName : `Wireless Device (${vid}:${pid})`;
  const maxIn = Number(candidate.maxIn ?? candidate.MaxIn ?? 0);
  const maxOut = Number(candidate.maxOut ?? candidate.MaxOut ?? 0);
  const maxFeat = Number(candidate.maxFeat ?? candidate.MaxFeat ?? 0);

  if (vid === "0x0000" || pid === "0x0000")
    return null;

  // Family 1: Verified CompX / PAW3950 OEM wireless receivers
  if (COMPX_VERIFIED_VIDS.has(vid) && maxIn >= 17 && (maxOut >= 17 || maxFeat >= 17)) {
    const inferred = inferCategory(productName);
    const category = inferred === "Other" ? "Mouse" : inferred;
    return {
      family: "CompX/PAW3950",
      profile: {
        name: productName,
        category,
        vendorIdHex: vid,
        productIdsHex: [pid],
        queryPacketHex: "08 03",
        useFeatureReport: maxOut < 17 && maxFeat >= 17,
        readPassiveInput: false,
        expectedReportId: 8,
        batteryByteIndex: 6,
        batteryMask: 255,
        chargingByteIndex: 7,
        chargingEqualsValue: 1,
        voltageHighByteIndex: 8,
        voltageLowByteIndex: 9,
        checksumMode: "Compx55",
        checksumByteIndex: 16,
        maxBatteryValue: 100
      }
    };
  }

  // Family 2: Ajazz / Sonix 64B feature protocol (0x0C45)
  if (vid === "0x0C45" && maxFeat >= 64 && /ajazz|mouse|aj\d/i.test(productName)) {
    return {
      family: "Ajazz/Sonix",
      profile: {
        name: productName,
        category: "Mouse",
        vendorIdHex: vid,
        productIdsHex: [pid],
        usagePageHex: "0xFF00",
        wakeupPacketHex: "00 F7",
        queryPacketHex: "05 40 00 01 00",
        useFeatureReport: true,
        readPassiveInput: false,
        expectedReportId: 5,
        batteryByteIndex: 4,
        batteryMask: 255,
        chargingByteIndex: 5,
        chargingEqualsValue: 1,
        checksumMode: "SumMod256",
        checksumByteIndex: 63,
        maxBatteryValue: 100
      }
    };
  }

  // Family 3: Keychron QMK 2.4GHz HID (0x3434)
  if (vid === "0x3434" && maxOut >= 32) {
    return {
      family: "Keychron/QMK",
      profile: {
        name: productName,
        category: "Keyboard",
        vendorIdHex: vid,
        productIdsHex: [pid],
        queryPacketHex: "00 80 05",
        useFeatureReport: false,
        readPassiveInput: false,
        expectedReportId: 0,
        batteryByteIndex: 3,
        batteryMask: 127,
        chargingByteIndex: 3,
        chargingMask: 128,
        maxBatteryValue: 100
      }
    };
  }

  // Family 4: Elan / PAW3950 OEM passive-push receivers & mice (0x04F3)
  if (
    vid === "0x04F3" &&
    (pid === "0x026F" ||
      pid === "0x026E" ||
      (maxFeat >= 136 && /highspeed receiver|gaming mouse|repel|x23|sm809|kojin|madmouse/i.test(productName)))
  ) {
    const isKnownPair = pid === "0x026F" || pid === "0x026E";
    return {
      family: "Elan/PAW3950-Passive",
      profile: {
        name: /highspeed receiver|gaming mouse wired/i.test(productName) ? "Throne Repel 8K" : productName,
        category: "Mouse",
        vendorIdHex: "0x04F3",
        productIdsHex: isKnownPair ? ["0x026F", "0x026E"] : [pid],
        wiredProductIdsHex: isKnownPair ? ["0x026E"] : [],
        usagePageHex: "0x000A",
        useFeatureReport: false,
        readPassiveInput: true,
        mergeProductIds: true,
        expectedReportId: 4,
        linkStatusByteIndex: 1,
        linkStatusOnlineValue: 1,
        batteryByteIndex: 4,
        batteryMask: 255,
        chargingByteIndex: 5,
        chargingEqualsValue: 1,
        passiveStaleSeconds: 25,
        maxBatteryValue: 100
      }
    };
  }

  return null;
}

export function parseUsbIds(usbIdsText, vidHex, pidHex) {
  const result = { vendorName: null, deviceName: null };
  if (!usbIdsText)
    return result;

  const targetVid = normalizeHex(vidHex).slice(2).toLowerCase();
  const targetPid = normalizeHex(pidHex).slice(2).toLowerCase();

  let inTargetVendor = false;
  const lines = usbIdsText.split(/\r?\n/);

  for (const line of lines) {
    if (!line || line.startsWith("#"))
      continue;

    if (!line.startsWith("\t") && !line.startsWith(" ")) {
      const m = /^([0-9a-f]{4})\s{2}(.+)$/i.exec(line);
      if (!m) {
        if (inTargetVendor)
          break;
        continue;
      }

      if (m[1].toLowerCase() === targetVid) {
        result.vendorName = m[2].trim();
        inTargetVendor = true;
      } else if (inTargetVendor) {
        break;
      }
    } else if (inTargetVendor && line.startsWith("\t") && !line.startsWith("\t\t")) {
      const dm = /^\t([0-9a-f]{4})\s{2}(.+)$/i.exec(line);
      if (dm && dm[1].toLowerCase() === targetPid) {
        result.deviceName = dm[2].trim();
        break;
      }
    }
  }

  return result;
}

export function buildResearchLinks(vidHex, pidHex, productName) {
  const vClean = normalizeHex(vidHex).slice(2);
  const pClean = normalizeHex(pidHex).slice(2);
  const encodedVidPid = encodeURIComponent(`${vClean}:${pClean}`);
  const encodedName = encodeURIComponent(productName || `${vClean}:${pClean}`);

  return {
    usbIdsDeviceHunt: `https://devicehunt.com/view/type/usb/vendor/${vClean}/device/${pClean}`,
    libratbagSearch: `https://github.com/libratbag/libratbag/search?q=${encodedVidPid}`,
    headsetControlSearch: `https://github.com/Sapd/HeadsetControl/search?q=${encodedVidPid}`,
    githubCodeSearch: `https://github.com/search?q=${encodedVidPid}+OR+${encodedName}&type=code`
  };
}

export function resolveCandidates(db, candidates, options = {}) {
  const usbIdsText = options.usbIdsText ?? "";
  const workingDb = structuredClone(db ?? { schemaVersion: 1, updatedAt: "", profiles: [] });
  if (!Array.isArray(workingDb.profiles))
    workingDb.profiles = [];

  const resolved = [];
  const unresolved = [];
  const alreadyCovered = [];
  const ignored = [];
  const seenInBatch = new Set();

  for (const raw of candidates ?? []) {
    const vid = normalizeHex(raw.vid ?? raw.Vid);
    const pid = normalizeHex(raw.pid ?? raw.Pid);
    const key = `${vid}:${pid}`;
    if (vid === "0x0000" || pid === "0x0000" || seenInBatch.has(key))
      continue;
    seenInBatch.add(key);

    const candidate = {
      vid,
      pid,
      productName: String(raw.productName ?? raw.ProductName ?? "Unknown").trim(),
      maxIn: Number(raw.maxIn ?? raw.MaxIn ?? 0),
      maxOut: Number(raw.maxOut ?? raw.MaxOut ?? 0),
      maxFeat: Number(raw.maxFeat ?? raw.MaxFeat ?? 0),
      ...(raw.issueNumber != null ? { issueNumber: Number(raw.issueNumber) } : {})
    };

    if (isNonBatteryNoise(candidate)) {
      ignored.push(candidate);
      continue;
    }

    if (isCoveredInDatabase(workingDb, vid, pid)) {
      alreadyCovered.push(candidate);
      continue;
    }

    const matched = matchKnownFamily(candidate);
    if (matched) {
      workingDb.profiles.push(matched.profile);
      resolved.push({
        vid,
        pid,
        family: matched.family,
        profile: matched.profile
      });
      continue;
    }

    const usbLookup = parseUsbIds(usbIdsText, vid, pid);
    const links = buildResearchLinks(vid, pid, candidate.productName);
    unresolved.push({
      ...candidate,
      usbVendorName: usbLookup.vendorName,
      usbDeviceName: usbLookup.deviceName,
      researchLinks: links
    });
  }

  if (resolved.length > 0) {
    workingDb.updatedAt = options.nowIso ?? new Date().toISOString();
  }

  return {
    db: workingDb,
    resolved,
    unresolved,
    alreadyCovered,
    ignored,
    dbUpdated: resolved.length > 0,
    totalProfiles: workingDb.profiles.length
  };
}

/**
 * Runs the 4-layer resolution pipeline:
 *  Layer 1 & 2 (sync deterministic) -> Layer 3 (OSS evidence lookup) -> Layer 4 (GitHub Models AI extraction -> PR proposal).
 */
export async function resolveCandidatesAsync(db, candidates, options = {}) {
  const syncResult = resolveCandidates(db, candidates, options);
  const aiProposals = [];
  const ignored = [...syncResult.ignored];
  const finalUnresolved = [];
  const maxResearchItems = Number.isFinite(options.maxResearchItems) ? options.maxResearchItems : 5;
  const nowIso = options.nowIso ?? new Date().toISOString();

  for (let i = 0; i < syncResult.unresolved.length; i++) {
    const item = syncResult.unresolved[i];
    if (options.noNetwork || i >= maxResearchItems) {
      finalUnresolved.push(item);
      continue;
    }

    const oss = await searchOpenSourceEvidence(item, {
      token: options.githubToken ?? "",
      fetchImpl: options.fetchImpl,
      maxSnippets: options.maxSnippets ?? 5,
      timeoutMs: options.ossTimeoutMs ?? 8000
    });

    const evidenceUrls = oss.evidence.map((e) => e.url);
    const enrichedItem = {
      ...item,
      evidenceUrls,
      evidenceCount: evidenceUrls.length
    };

    if (oss.evidence.length === 0 || !options.enableAi || !options.githubToken) {
      finalUnresolved.push(enrichedItem);
      continue;
    }

    const ai = await extractProtocolWithAi(item, oss.evidence, {
      token: options.githubToken,
      fetchImpl: options.fetchImpl,
      models: options.aiModels,
      endpointUrl: options.aiEndpointUrl,
      timeoutMs: options.aiTimeoutMs ?? 20000
    });

    if (ai.accepted && ai.profile) {
      const proposalDb = structuredClone(syncResult.db);
      proposalDb.profiles.push(ai.profile);
      proposalDb.updatedAt = nowIso;

      aiProposals.push({
        vid: item.vid,
        pid: item.pid,
        productName: item.productName,
        confidence: ai.confidence,
        reason: ai.reason,
        sourceUrls: ai.sourceUrls,
        model: ai.model,
        profile: ai.profile,
        ...(item.issueNumber != null ? { issueNumber: item.issueNumber } : {}),
        updatedDb: proposalDb
      });
      continue;
    }

    if (ai.isNonBattery) {
      ignored.push({
        ...enrichedItem,
        ignoredReason: "ai_confirmed_non_battery",
        aiReason: ai.reason,
        sourceUrls: ai.sourceUrls ?? []
      });
      continue;
    }

    finalUnresolved.push({
      ...enrichedItem,
      aiSummary: ai.reason
    });
  }

  return {
    db: syncResult.db,
    resolved: syncResult.resolved,
    aiProposals,
    unresolved: finalUnresolved,
    alreadyCovered: syncResult.alreadyCovered,
    ignored,
    dbUpdated: syncResult.dbUpdated,
    totalProfiles: syncResult.totalProfiles
  };
}

async function fetchUsbIdsSafe() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 6000);
  try {
    const resp = await fetch(USB_IDS_URL, {
      signal: controller.signal,
      headers: { "user-agent": "BatteryWidget-SelfHealing/1.0" }
    });
    if (!resp.ok)
      return "";
    return await resp.text();
  } catch {
    return "";
  } finally {
    clearTimeout(timer);
  }
}

function parseCliArgs(argv) {
  const args = {
    db: "profiles-db.json",
    payloadFile: null,
    payloadJson: null,
    issuesFile: null,
    out: null,
    writeProposalDir: null,
    noNetwork: false,
    enableAi: false
  };

  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--db" && argv[i + 1]) args.db = argv[++i];
    else if (a === "--payload-file" && argv[i + 1]) args.payloadFile = argv[++i];
    else if (a === "--payload" && argv[i + 1]) args.payloadJson = argv[++i];
    else if (a === "--issues-file" && argv[i + 1]) args.issuesFile = argv[++i];
    else if (a === "--out" && argv[i + 1]) args.out = argv[++i];
    else if (a === "--write-proposal-dir" && argv[i + 1]) args.writeProposalDir = argv[++i];
    else if (a === "--no-network") args.noNetwork = true;
    else if (a === "--enable-ai") args.enableAi = true;
  }

  return args;
}

async function main() {
  const args = parseCliArgs(process.argv.slice(2));
  const dbPath = path.resolve(args.db);
  const db = JSON.parse(fs.readFileSync(dbPath, "utf8"));

  let rawCandidates = [];
  if (args.issuesFile) {
    const issues = JSON.parse(fs.readFileSync(path.resolve(args.issuesFile), "utf8"));
    rawCandidates = parseIssuesToCandidates(Array.isArray(issues) ? issues : []);
  } else {
    let payload = {};
    if (args.payloadFile) {
      payload = JSON.parse(fs.readFileSync(path.resolve(args.payloadFile), "utf8"));
    } else if (args.payloadJson) {
      payload = JSON.parse(args.payloadJson);
    }

    rawCandidates = Array.isArray(payload)
      ? payload
      : (payload.candidates ?? payload.UnknownHidCandidates ?? payload.unknownHidCandidates ?? []);
  }

  const usbIdsText = args.noNetwork || rawCandidates.length === 0
    ? ""
    : await fetchUsbIdsSafe();

  const result = await resolveCandidatesAsync(db, rawCandidates, {
    usbIdsText,
    noNetwork: args.noNetwork,
    enableAi: args.enableAi,
    githubToken: process.env.GITHUB_TOKEN ?? ""
  });

  if (result.dbUpdated) {
    fs.writeFileSync(dbPath, JSON.stringify(result.db, null, 2) + "\n", "utf8");
  }

  if (args.writeProposalDir && result.aiProposals.length > 0) {
    const propDir = path.resolve(args.writeProposalDir);
    fs.mkdirSync(propDir, { recursive: true });
    for (const prop of result.aiProposals) {
      const slug = `${prop.vid.slice(2)}-${prop.pid.slice(2)}`.toLowerCase();
      const dbOutPath = path.join(propDir, `proposal-${slug}.profiles-db.json`);
      fs.writeFileSync(dbOutPath, JSON.stringify(prop.updatedDb, null, 2) + "\n", "utf8");
    }
  }

  const slimProposals = result.aiProposals.map(({ updatedDb: _omit, ...rest }) => rest);

  const summary = {
    resolved: result.resolved,
    aiProposals: slimProposals,
    aiProposalCount: slimProposals.length,
    unresolved: result.unresolved,
    alreadyCovered: result.alreadyCovered,
    ignored: result.ignored,
    dbUpdated: result.dbUpdated,
    totalProfiles: result.totalProfiles
  };

  if (args.out) {
    fs.writeFileSync(path.resolve(args.out), JSON.stringify(summary, null, 2), "utf8");
  }

  console.log(JSON.stringify(summary, null, 2));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((err) => {
    console.error(err);
    process.exit(1);
  });
}
