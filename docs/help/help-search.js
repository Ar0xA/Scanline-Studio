(function (global) {
  "use strict";

  function normalized(value) {
    return value
      .normalize("NFKD")
      .replace(/[\u0300-\u036f]/g, "")
      .toLocaleLowerCase()
      .replace(/[^a-z0-9+.#/-]+/g, " ")
      .trim();
  }

  function scoreEntry(entry, terms) {
    let score = 0;
    for (const term of terms) {
      if (!term) {
        continue;
      }

      let termMatched = false;
      if (entry.titleNormalized === term) {
        score += 100;
        termMatched = true;
      } else if (entry.titleNormalized.startsWith(term)) {
        score += 55;
        termMatched = true;
      } else if (entry.titleNormalized.includes(term)) {
        score += 35;
        termMatched = true;
      }

      if (entry.keywordsNormalized.includes(term)) {
        score += 24;
        termMatched = true;
      }
      if (entry.categoryNormalized.includes(term)) {
        score += 12;
        termMatched = true;
      }
      if (entry.bodyNormalized.includes(term)) {
        score += 5;
        termMatched = true;
      }
      if (!termMatched) {
        return 0;
      }
    }
    return score;
  }

  function performSearch(searchIndex, query, limit = 12) {
    const terms = normalized(query).split(/\s+/).filter(Boolean);
    if (terms.length === 0) {
      return { terms, matches: [] };
    }

    const matches = searchIndex
      .map((entry) => ({ entry, score: scoreEntry(entry, terms) }))
      .filter((match) => match.score > 0)
      .sort((left, right) => right.score - left.score || left.entry.documentOrder - right.entry.documentOrder)
      .slice(0, limit)
      .map((match) => match.entry);

    return { terms, matches };
  }

  global.ScanlineHelpSearch = Object.freeze({ normalized, performSearch });
})(globalThis);
