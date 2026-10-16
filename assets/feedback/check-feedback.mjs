// Runs the real form.js key decoder (not a C# copy of it) and checks the result against the
// access key's SHA-256, so a broken mask or blob fails here instead of on a user's submit.
// Usage: node assets/feedback/check-feedback.mjs
import { createRequire } from "node:module";
import { createHash } from "node:crypto";

const ACCESS_KEY_SHA256 = "7c83c66b86c55348de8f6f3d49dfd82118a55162acd14b0bd83f07cac5b36c31";

globalThis.atob ??= (b64) => Buffer.from(b64, "base64").toString("binary");
const require = createRequire(import.meta.url);
const { unmask, KEY_BLOB } = require("./form.js");

const hash = createHash("sha256").update(unmask(KEY_BLOB), "utf8").digest("hex");
if (hash !== ACCESS_KEY_SHA256) {
  console.error("FAIL: form.js decodes to a key whose SHA-256 does not match the expected access key.");
  process.exit(1);
}
console.log("OK: form.js decodes to the expected access key.");
