import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const helpDirectory = path.dirname(fileURLToPath(import.meta.url));
const htmlPath = path.join(helpDirectory, "index.html");
const html = fs.readFileSync(htmlPath, "utf8");
const failures = [];

function fail(message) {
  failures.push(message);
}

const topicPattern = /<article\s+id="([^"]+)"\s+class="[^"]*help-topic[^"]*"\s+data-help-topic\s+data-category="([^"]+)"\s+data-keywords="([^"]*)"[\s\S]*?<h2>([^<]+)<\/h2>[\s\S]*?<\/article>/g;
const topics = [];
for (const match of html.matchAll(topicPattern)) {
  topics.push({ id: match[1], category: match[2], keywords: match[3], title: match[4], html: match[0] });
}

if (topics.length < 28) {
  fail(`Expected at least 28 help topics, found ${topics.length}.`);
}

const ids = new Set();
for (const topic of topics) {
  if (ids.has(topic.id)) {
    fail(`Duplicate topic id: ${topic.id}`);
  }
  ids.add(topic.id);
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(topic.id)) {
    fail(`Topic id is not URL-safe: ${topic.id}`);
  }
  if (!topic.category.trim()) {
    fail(`Topic ${topic.id} has no category.`);
  }
  if (!topic.keywords.trim()) {
    fail(`Topic ${topic.id} has no search keywords.`);
  }
}

const allDocumentIds = Array.from(html.matchAll(/\sid="([^"]+)"/g), (match) => match[1]);
const documentIds = new Set(allDocumentIds);
if (documentIds.size !== allDocumentIds.length) {
  const duplicates = [...new Set(allDocumentIds.filter((id, index) => allDocumentIds.indexOf(id) !== index))];
  fail(`Duplicate document id(s): ${duplicates.join(", ")}`);
}
for (const match of html.matchAll(/href="#([^"]+)"/g)) {
  if (!documentIds.has(match[1])) {
    fail(`Broken internal link: #${match[1]}`);
  }
}

for (const match of html.matchAll(/(?:href|src)="([^"#]+)"/g)) {
  const reference = match[1];
  if (/^(?:https?:|mailto:|data:)/.test(reference)) {
    continue;
  }
  const localPath = path.join(helpDirectory, reference);
  if (!fs.existsSync(localPath)) {
    fail(`Missing local asset: ${reference}`);
  }
}

const searchContext = vm.createContext({});
const searchSource = fs.readFileSync(path.join(helpDirectory, "help-search.js"), "utf8");
vm.runInContext(searchSource, searchContext, { filename: "help-search.js" });
const searchEngine = searchContext.ScanlineHelpSearch;
if (!searchEngine) {
  fail("Production search engine did not load.");
}

const searchIndex = topics.map((topic, documentOrder) => {
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

if (!/<meta\s+name="help-version"\s+content="[^"]+">/.test(html)) {
  fail("Missing help-version metadata.");
}

if ((html.match(/data-help-version/g) ?? []).length < 2) {
  fail("Visible guide revisions are not sourced from help-version metadata.");
}

if (failures.length > 0) {
  console.error(`Help validation failed (${failures.length}):`);
  for (const failure of failures) {
    console.error(`- ${failure}`);
  }
  process.exitCode = 1;
} else {
  console.log(`Help validation passed: ${topics.length} topics, ${documentIds.size} document IDs.`);
}
