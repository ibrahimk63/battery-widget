const GITHUB_API_BASE = "https://api.github.com";

function cleanHex4(value) {
  const cleaned = String(value ?? "")
    .trim()
    .replace(/^0x/i, "")
    .replace(/[^0-9a-f]/gi, "");
  if (!cleaned)
    return "0000";
  const num = Number.parseInt(cleaned, 16);
  if (!Number.isFinite(num) || num < 0)
    return "0000";
  return num.toString(16).toLowerCase().padStart(4, "0");
}

function truncateText(text, maxLen) {
  const s = String(text ?? "").replace(/\r\n/g, "\n").trim();
  if (s.length <= maxLen)
    return s;
  return `${s.slice(0, maxLen - 3)}...`;
}

function extractRepoFromUrl(htmlUrl, repositoryUrl) {
  if (repositoryUrl) {
    const m = /\/repos\/([^/]+\/[^/]+)$/i.exec(repositoryUrl);
    if (m) return m[1];
  }
  const m = /^https?:\/\/github\.com\/([^/]+\/[^/]+)\//i.exec(String(htmlUrl ?? ""));
  return m ? m[1] : "unknown/repo";
}

function buildGithubHeaders(token, accept = "application/vnd.github+json") {
  const headers = {
    "User-Agent": "BatteryWidget-SelfHealing/2.0",
    "Accept": accept,
    "X-GitHub-Api-Version": "2022-11-28"
  };
  if (token && String(token).trim()) {
    headers["Authorization"] = `Bearer ${String(token).trim()}`;
  }
  return headers;
}

async function fetchJsonSafe(fetchImpl, url, headers, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const resp = await fetchImpl(url, { headers, signal: controller.signal });
    if (!resp.ok) {
      return { ok: false, status: resp.status, data: null };
    }
    const data = await resp.json();
    return { ok: true, status: resp.status, data };
  } catch (err) {
    return { ok: false, status: 0, error: err?.message ?? "fetch_failed", data: null };
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Parses a libratbag `.device` INI file and extracts Name & Driver if DeviceMatch contains usb:<vid>:<pid>.
 */
export function parseLibratbagDeviceIni(iniText, vidHex, pidHex) {
  if (!iniText)
    return null;

  const targetVid = cleanHex4(vidHex);
  const targetPid = cleanHex4(pidHex);
  const targetPair = `${targetVid}:${targetPid}`;

  let name = null;
  let driver = null;
  let deviceMatchRaw = "";

  for (const rawLine of String(iniText).split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#") || line.startsWith(";") || line.startsWith("["))
      continue;
    const eqIdx = line.indexOf("=");
    if (eqIdx <= 0)
      continue;
    const key = line.slice(0, eqIdx).trim();
    const val = line.slice(eqIdx + 1).trim();

    if (key === "Name") name = val;
    else if (key === "Driver") driver = val;
    else if (key === "DeviceMatch") deviceMatchRaw = val;
  }

  if (!deviceMatchRaw)
    return null;

  const matches = deviceMatchRaw
    .split(";")
    .map((m) => m.trim().toLowerCase())
    .filter(Boolean);

  const hasMatch = matches.some((entry) => {
    const m = /^usb:([0-9a-f]{4}):([0-9a-f]{4})(?:[:;].*)?$/i.exec(entry);
    if (!m) return false;
    return `${m[1]}:${m[2]}` === targetPair;
  });

  if (!hasMatch)
    return null;

  return { name, driver };
}

async function appendIssueItemEvidence(evidenceMap, item, isTopHit, fetchImpl, headers, timeoutMs) {
  const url = item?.html_url;
  if (!url || evidenceMap.has(url))
    return;

  const repo = extractRepoFromUrl(url, item.repository_url);
  const title = String(item.title ?? "Issue").trim();
  let combined = `${title}\n\n${String(item.body ?? "").trim()}`;

  if (isTopHit && Number(item.comments ?? 0) > 0 && item.comments_url) {
    const commentsRes = await fetchJsonSafe(
      fetchImpl,
      `${item.comments_url}?per_page=3`,
      headers,
      timeoutMs
    );
    if (commentsRes.ok && Array.isArray(commentsRes.data) && commentsRes.data.length > 0) {
      const commentTexts = commentsRes.data
        .slice(0, 3)
        .map((c, idx) => `--- Comment ${idx + 1} ---\n${truncateText(c?.body ?? "", 700)}`)
        .join("\n\n");
      combined = `${combined}\n\n${commentTexts}`;
    }
  }

  evidenceMap.set(url, {
    source: "github-issue",
    repo,
    title,
    url,
    text: truncateText(combined, 2500)
  });
}

/**
 * Queries public open-source GitHub Issues, targeted peripheral battery repos,
 * and (when authenticated) GitHub Code Search for HID protocol evidence.
 */
export async function searchOpenSourceEvidence(candidate, options = {}) {
  const token = options.token ?? "";
  const fetchImpl = options.fetchImpl ?? fetch;
  const maxSnippets = Number.isFinite(options.maxSnippets) ? options.maxSnippets : 5;
  const timeoutMs = Number.isFinite(options.timeoutMs) ? options.timeoutMs : 8000;
  const maxTotalChars = Number.isFinite(options.maxTotalChars) ? options.maxTotalChars : 14000;

  const vHex = cleanHex4(candidate?.vid ?? candidate?.Vid);
  const pHex = cleanHex4(candidate?.pid ?? candidate?.Pid);
  const searchErrors = [];

  if (vHex === "0000" || pHex === "0000") {
    return { evidence: [], searchErrors: ["invalid_vid_pid"] };
  }

  const headers = buildGithubHeaders(token);
  const evidenceMap = new Map();

  // Query 1: Broad public GitHub Issues / PRs matching VID:PID + battery
  const q1 = `"${vHex}:${pHex}" OR ("0x${vHex}" "0x${pHex}") battery`;
  const url1 = `${GITHUB_API_BASE}/search/issues?q=${encodeURIComponent(q1)}&per_page=5`;
  const res1 = await fetchJsonSafe(fetchImpl, url1, headers, timeoutMs);

  const q1Items = res1.ok && Array.isArray(res1.data?.items) ? res1.data.items : [];
  if (!res1.ok) {
    searchErrors.push(`issues_search_1:${res1.status || res1.error}`);
  } else {
    for (let i = 0; i < Math.min(3, q1Items.length); i++) {
      if (evidenceMap.size >= maxSnippets) break;
      await appendIssueItemEvidence(evidenceMap, q1Items[i], i === 0, fetchImpl, headers, timeoutMs);
    }
  }

  // Query 2: Targeted open-source battery repositories (if Query 1 returned fewer than 3 hits)
  if (q1Items.length < 3 && evidenceMap.size < maxSnippets) {
    const q2 = `(repo:DillanCidral/Joule OR repo:wushuai0802/myfinder-device-battery OR repo:Sapd/HeadsetControl OR repo:libratbag/libratbag) (${pHex} OR "${vHex}:${pHex}")`;
    const url2 = `${GITHUB_API_BASE}/search/issues?q=${encodeURIComponent(q2)}&per_page=4`;
    const res2 = await fetchJsonSafe(fetchImpl, url2, headers, timeoutMs);

    if (!res2.ok) {
      searchErrors.push(`issues_search_2:${res2.status || res2.error}`);
    } else if (Array.isArray(res2.data?.items)) {
      for (let i = 0; i < res2.data.items.length; i++) {
        if (evidenceMap.size >= maxSnippets) break;
        await appendIssueItemEvidence(evidenceMap, res2.data.items[i], i === 0, fetchImpl, headers, timeoutMs);
      }
    }
  }

  // Query 3: GitHub Code Search (authenticated best-effort with graceful fallback)
  if (token && String(token).trim() && evidenceMap.size < maxSnippets) {
    const codeHeaders = buildGithubHeaders(token, "application/vnd.github.text-match+json");
    const q3 = `"0x${vHex}" "0x${pHex}" battery`;
    const url3 = `${GITHUB_API_BASE}/search/code?q=${encodeURIComponent(q3)}&per_page=3`;
    const res3 = await fetchJsonSafe(fetchImpl, url3, codeHeaders, timeoutMs);

    if (!res3.ok) {
      searchErrors.push(`code_search:${res3.status || res3.error}`);
    } else if (Array.isArray(res3.data?.items)) {
      for (const codeItem of res3.data.items) {
        if (evidenceMap.size >= maxSnippets) break;
        const url = codeItem?.html_url;
        if (!url || evidenceMap.has(url)) continue;

        const repo = codeItem.repository?.full_name ?? extractRepoFromUrl(url);
        const filePath = codeItem.path ?? "code";
        const fragments = Array.isArray(codeItem.text_matches)
          ? codeItem.text_matches.map((m) => String(m?.fragment ?? "").trim()).filter(Boolean)
          : [];

        let snippetText = fragments.join("\n---\n");
        if (!snippetText && codeItem.url) {
          const rawRes = await fetchJsonSafe(
            fetchImpl,
            codeItem.url,
            buildGithubHeaders(token, "application/vnd.github.raw+json"),
            timeoutMs
          );
          if (rawRes.ok && typeof rawRes.data === "string") {
            snippetText = rawRes.data;
          }
        }

        if (snippetText) {
          evidenceMap.set(url, {
            source: "github-code",
            repo,
            title: `${repo}:${filePath}`,
            url,
            text: truncateText(snippetText, 3000)
          });
        }
      }
    }
  }

  // Enforce combined character budget across returned snippets
  const evidence = [];
  let remainingChars = maxTotalChars;
  for (const entry of evidenceMap.values()) {
    if (evidence.length >= maxSnippets || remainingChars <= 100)
      break;
    const boundedText = truncateText(entry.text, Math.min(entry.text.length, remainingChars));
    remainingChars -= boundedText.length;
    evidence.push({ ...entry, text: boundedText });
  }

  return { evidence, searchErrors };
}
