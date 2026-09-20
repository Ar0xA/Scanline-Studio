import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const helpDirectory = path.dirname(fileURLToPath(import.meta.url));
const failures = [];

function fail(message) {
  failures.push(message);
}

// Every translated guide is a subdirectory of docs/help/ holding its own index.html (R7:
// docs/help/README.md's "Updating the guide" section). English stays the root index.html.
function discoverGuides() {
  const guides = [{ code: "en", dir: helpDirectory }];
  for (const entry of fs.readdirSync(helpDirectory, { withFileTypes: true })) {
    if (!entry.isDirectory()) {
      continue;
    }
    const dir = path.join(helpDirectory, entry.name);
    if (fs.existsSync(path.join(dir, "index.html"))) {
      guides.push({ code: entry.name, dir });
    }
  }
  return guides;
}

const topicPattern = /<article\s+id="([^"]+)"\s+class="[^"]*help-topic[^"]*"\s+data-help-topic\s+data-category="([^"]+)"\s+data-keywords="([^"]*)"[\s\S]*?<h2>([^<]+)<\/h2>[\s\S]*?<\/article>/g;

// Runs every structural check against one guide's index.html and returns its topic IDs, for the
// cross-language drift check that runs once all guides have been checked individually.
function checkGuideStructure(guide) {
  const htmlPath = path.join(guide.dir, "index.html");
  const html = fs.readFileSync(htmlPath, "utf8");
  const label = `[${guide.code}]`;

  const topics = [];
  for (const match of html.matchAll(topicPattern)) {
    topics.push({ id: match[1], category: match[2], keywords: match[3], title: match[4], html: match[0] });
  }

  if (guide.code === "en" && topics.length < 28) {
    fail(`${label} Expected at least 28 help topics, found ${topics.length}.`);
  }

  const ids = new Set();
  for (const topic of topics) {
    if (ids.has(topic.id)) {
      fail(`${label} Duplicate topic id: ${topic.id}`);
    }
    ids.add(topic.id);
    if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(topic.id)) {
      fail(`${label} Topic id is not URL-safe: ${topic.id}`);
    }
    if (!topic.category.trim()) {
      fail(`${label} Topic ${topic.id} has no category.`);
    }
    if (!topic.keywords.trim()) {
      fail(`${label} Topic ${topic.id} has no search keywords.`);
    }
  }

  const allDocumentIds = Array.from(html.matchAll(/\sid="([^"]+)"/g), (match) => match[1]);
  const documentIds = new Set(allDocumentIds);
  if (documentIds.size !== allDocumentIds.length) {
    const duplicates = [...new Set(allDocumentIds.filter((id, index) => allDocumentIds.indexOf(id) !== index))];
    fail(`${label} Duplicate document id(s): ${duplicates.join(", ")}`);
  }
  for (const match of html.matchAll(/href="#([^"]+)"/g)) {
    if (!documentIds.has(match[1])) {
      fail(`${label} Broken internal link: #${match[1]}`);
    }
  }

  for (const match of html.matchAll(/(?:href|src)="([^"#]+)"/g)) {
    const reference = match[1];
    if (/^(?:https?:|mailto:|data:)/.test(reference)) {
      continue;
    }
    const localPath = path.join(guide.dir, reference);
    if (!fs.existsSync(localPath)) {
      fail(`${label} Missing local asset: ${reference}`);
    }
  }

  if (!/<meta\s+name="help-version"\s+content="[^"]+">/.test(html)) {
    fail(`${label} Missing help-version metadata.`);
  }
  if ((html.match(/data-help-version/g) ?? []).length < 2) {
    fail(`${label} Visible guide revisions are not sourced from help-version metadata.`);
  }

  const chromeStringsBlock = html.match(/window\.ScanlineHelpStrings\s*=\s*\{([\s\S]*?)\};/)?.[1] ?? "";
  for (const key of ["noResultsTemplate", "unversioned"]) {
    const value = chromeStringsBlock.match(new RegExp(`${key}\\s*:\\s*"([^"]+)"`))?.[1];
    if (!value?.trim()) {
      fail(`${label} Missing or empty ScanlineHelpStrings.${key}.`);
    }
  }

  return { html, topics, topicIds: ids };
}

const guides = discoverGuides();
const checked = guides.map((guide) => ({ ...guide, ...checkGuideStructure(guide) }));

// A translated guide that drops or adds a topic relative to English has silently drifted out of
// sync (R7) -- the whole point of keying the guide to the interface language is that every
// language covers the same topics.
const englishGuide = checked.find((guide) => guide.code === "en");
for (const guide of checked) {
  if (guide.code === "en") {
    continue;
  }
  for (const id of guide.topicIds) {
    if (!englishGuide.topicIds.has(id)) {
      fail(`[${guide.code}] Topic "${id}" does not exist in the English guide.`);
    }
  }
  for (const id of englishGuide.topicIds) {
    if (!guide.topicIds.has(id)) {
      fail(`[${guide.code}] Missing topic "${id}" present in the English guide.`);
    }
  }
}

// The search engine, and the literal English search-phrase expectations below, only apply to the
// English guide -- translated guides have their own wording.
const searchContext = vm.createContext({});
const searchSource = fs.readFileSync(path.join(helpDirectory, "help-search.js"), "utf8");
vm.runInContext(searchSource, searchContext, { filename: "help-search.js" });
const searchEngine = searchContext.ScanlineHelpSearch;
if (!searchEngine) {
  fail("Production search engine did not load.");
}

if (searchEngine) {
  const searchIndex = englishGuide.topics.map((topic, documentOrder) => {
    const body = topic.html.replace(/<[^>]+>/g, " ");
    return {
      ...topic,
      titleNormalized: searchEngine.normalized(topic.title),
      categoryNormalized: searchEngine.normalized(topic.category),
      keywordsNormalized: searchEngine.normalized(topic.keywords),
      bodyNormalized: searchEngine.normalized(body),
      documentOrder
    };
  });

  const searchExpectations = new Map([
    ["cat ptt", "radio-cat"],
    ["slanted image", "troubleshooting"],
    ["template overlays", "templates-overlays"],
    ["qrz password", "qrz-forwarding"],
    ["wav re-decode", "record-redecode"],
    ["dark mode font size", "appearance-options"],
    ["stations heard", "receive-controls"]
  ]);

  for (const [query, expectedTopic] of searchExpectations) {
    const matches = searchEngine.performSearch(searchIndex, query, 12).matches;
    if (!matches.some((topic) => topic.id === expectedTopic)) {
      fail(`Search expectation "${query}" did not include #${expectedTopic}.`);
    }
  }

  const exactTitleMatch = searchEngine.performSearch(searchIndex, "Radio and CAT setup", 12).matches[0];
  if (exactTitleMatch?.id !== "radio-cat") {
    fail("Production search did not rank an exact topic-title query first.");
  }
}

if (failures.length > 0) {
  console.error(`Help validation failed (${failures.length}):`);
  for (const failure of failures) {
    console.error(`- ${failure}`);
  }
  process.exitCode = 1;
} else {
  const guideSummary = checked.map((guide) => `${guide.code} (${guide.topics.length} topics)`).join(", ");
  console.log(`Help validation passed: ${guideSummary}.`);
}
