(function () {
  "use strict";

  const topics = Array.from(document.querySelectorAll("article[data-help-topic]"));
  const toc = document.querySelector("#help-toc");
  const searchInput = document.querySelector("#help-search");
  const searchResults = document.querySelector("#search-results");
  const menuToggle = document.querySelector("#menu-toggle");
  const sidebar = document.querySelector("#help-sidebar");
  const main = document.querySelector("#main-content");
  const mobileNavigation = window.matchMedia("(max-width: 880px)");
  const searchEngine = globalThis.ScanlineHelpSearch;
  let selectedResult = -1;
  let renderedResults = [];

  if (!searchEngine) {
    throw new Error("The help search engine was not loaded.");
  }

  const { normalized, performSearch } = searchEngine;

  function topicTitle(topic) {
    return topic.querySelector("h2")?.textContent.trim() || topic.id;
  }

  function topicCategory(topic) {
    return topic.dataset.category || "Other";
  }

  function buildTableOfContents() {
    if (!toc) {
      return;
    }

    const groups = new Map();
    for (const topic of topics) {
      const category = topicCategory(topic);
      if (!groups.has(category)) {
        groups.set(category, []);
      }
      groups.get(category).push(topic);
    }

    const fragment = document.createDocumentFragment();
    for (const [category, categoryTopics] of groups) {
      const section = document.createElement("section");
      section.className = "toc-group";

      const heading = document.createElement("h3");
      heading.className = "toc-group-title";
      heading.textContent = category;
      section.appendChild(heading);

      const list = document.createElement("ul");
      list.className = "toc-list";
      for (const topic of categoryTopics) {
        const item = document.createElement("li");
        const link = document.createElement("a");
        link.className = "toc-link";
        link.href = `#${topic.id}`;
        link.dataset.topicId = topic.id;
        link.textContent = topicTitle(topic);
        item.appendChild(link);
        list.appendChild(item);
      }
      section.appendChild(list);
      fragment.appendChild(section);
    }

    toc.replaceChildren(fragment);
  }

  const searchIndex = topics.map((topic, documentOrder) => {
    const title = topicTitle(topic);
    const category = topicCategory(topic);
    const keywords = topic.dataset.keywords || "";
    const body = Array.from(topic.querySelectorAll("p, li, td, dt, dd, h3, h4"))
      .map((element) => element.textContent.trim())
      .join(" ");

    return {
      topic,
      title,
      category,
      keywords,
      body,
      titleNormalized: normalized(title),
      categoryNormalized: normalized(category),
      keywordsNormalized: normalized(keywords),
      bodyNormalized: normalized(body),
      documentOrder
    };
  });

  function makeSnippet(entry, terms) {
    const body = entry.body.replace(/\s+/g, " ").trim();
    const bodyLower = body.toLocaleLowerCase();
    let matchIndex = -1;
    for (const term of terms) {
      const candidate = bodyLower.indexOf(term.toLocaleLowerCase());
      if (candidate >= 0 && (matchIndex < 0 || candidate < matchIndex)) {
        matchIndex = candidate;
      }
    }

    const radius = 85;
    const start = Math.max(0, matchIndex < 0 ? 0 : matchIndex - radius);
    const end = Math.min(body.length, start + radius * 2);
    return `${start > 0 ? "…" : ""}${body.slice(start, end).trim()}${end < body.length ? "…" : ""}`;
  }

  function setSelectedResult(index) {
    if (renderedResults.length === 0) {
      selectedResult = -1;
      searchInput?.removeAttribute("aria-activedescendant");
      return;
    }

    selectedResult = Math.max(0, Math.min(index, renderedResults.length - 1));
    renderedResults.forEach((button, buttonIndex) => {
      button.setAttribute("aria-selected", buttonIndex === selectedResult ? "true" : "false");
    });
    const selected = renderedResults[selectedResult];
    searchInput?.setAttribute("aria-activedescendant", selected.id);
    selected.scrollIntoView({ block: "nearest" });
  }

  function closeSearch() {
    if (!searchResults || !searchInput) {
      return;
    }
    searchResults.hidden = true;
    searchInput.setAttribute("aria-expanded", "false");
    searchInput.removeAttribute("aria-activedescendant");
    renderedResults = [];
    selectedResult = -1;
  }

  function openTopic(topicId) {
    closeSearch();
    setNavigationState(false);
    window.location.hash = topicId;
    const target = document.getElementById(topicId);
    if (target) {
      target.setAttribute("tabindex", "-1");
      target.focus({ preventScroll: true });
      target.scrollIntoView({ block: "start" });
    }
  }

  function renderSearchResults(query) {
    if (!searchResults || !searchInput) {
      return;
    }

    const search = performSearch(searchIndex, query);
    const matches = search.matches.map((match) => ({ ...match, snippet: makeSnippet(match, search.terms) }));
    const fragment = document.createDocumentFragment();
    searchInput.removeAttribute("aria-activedescendant");
    renderedResults = [];
    selectedResult = -1;

    if (matches.length === 0) {
      const empty = document.createElement("p");
      empty.className = "search-empty";
      empty.textContent = `No help topics found for “${query.trim()}”. Try fewer or different words.`;
      fragment.appendChild(empty);
    } else {
      matches.forEach((match, index) => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "search-result";
        button.id = `help-search-result-${index}`;
        button.setAttribute("role", "option");
        button.setAttribute("aria-selected", "false");
        button.dataset.topicId = match.topic.id;

        const title = document.createElement("span");
        title.className = "search-result-title";
        title.textContent = match.title;

        const meta = document.createElement("span");
        meta.className = "search-result-meta";
        meta.textContent = match.category;

        const snippet = document.createElement("span");
        snippet.className = "search-result-snippet";
        snippet.textContent = match.snippet;

        button.append(title, meta, snippet);
        button.addEventListener("click", () => openTopic(match.topic.id));
        fragment.appendChild(button);
        renderedResults.push(button);
      });
    }

    searchResults.replaceChildren(fragment);
    searchResults.hidden = false;
    searchInput.setAttribute("aria-expanded", "true");
  }

  function updateCurrentTopic(topicId) {
    document.querySelectorAll(".toc-link").forEach((link) => {
      if (link.dataset.topicId === topicId) {
        link.setAttribute("aria-current", "location");
      } else {
        link.removeAttribute("aria-current");
      }
    });
  }

  function setNavigationState(open) {
    document.body.classList.toggle("nav-open", open);
    menuToggle?.setAttribute("aria-expanded", String(open));

    if (!sidebar) {
      return;
    }
    const concealed = mobileNavigation.matches && !open;
    sidebar.toggleAttribute("inert", concealed);
    if (concealed) {
      sidebar.setAttribute("aria-hidden", "true");
    } else {
      sidebar.removeAttribute("aria-hidden");
    }
  }

  buildTableOfContents();

  searchInput?.addEventListener("input", () => {
    const query = searchInput.value;
    if (normalized(query).length < 2) {
      closeSearch();
      return;
    }
    renderSearchResults(query);
  });

  searchInput?.addEventListener("keydown", (event) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setSelectedResult(selectedResult + 1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setSelectedResult(selectedResult <= 0 ? renderedResults.length - 1 : selectedResult - 1);
    } else if (event.key === "Enter" && selectedResult >= 0) {
      event.preventDefault();
      renderedResults[selectedResult].click();
    } else if (event.key === "Escape") {
      closeSearch();
      searchInput.blur();
    }
  });

  document.addEventListener("keydown", (event) => {
    const target = event.target;
    const isTyping = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target?.isContentEditable;
    if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === "k") {
      event.preventDefault();
      searchInput?.focus();
      searchInput?.select();
    } else if (event.key === "/" && !isTyping) {
      event.preventDefault();
      searchInput?.focus();
    } else if (event.key === "Escape" && document.body.classList.contains("nav-open")) {
      setNavigationState(false);
      menuToggle?.focus();
    }
  });

  document.addEventListener("click", (event) => {
    if (!searchResults?.hidden && !event.target.closest(".search-shell")) {
      closeSearch();
    }
  });

  menuToggle?.addEventListener("click", () => {
    setNavigationState(!document.body.classList.contains("nav-open"));
  });

  toc?.addEventListener("click", (event) => {
    if (event.target.closest("a")) {
      setNavigationState(false);
    }
  });

  mobileNavigation.addEventListener("change", () => setNavigationState(false));

  if ("IntersectionObserver" in window) {
    const visibleTopics = new Map();
    const observer = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            visibleTopics.set(entry.target.id, entry.boundingClientRect.top);
          } else {
            visibleTopics.delete(entry.target.id);
          }
        });

        if (visibleTopics.size > 0) {
          const nearest = Array.from(visibleTopics.entries()).sort((left, right) => Math.abs(left[1]) - Math.abs(right[1]))[0];
          updateCurrentTopic(nearest[0]);
        }
      },
      { rootMargin: "-18% 0px -65% 0px", threshold: [0, 0.1] }
    );
    topics.forEach((topic) => observer.observe(topic));
  }

  window.addEventListener("hashchange", () => {
    const topicId = window.location.hash.slice(1);
    if (topicId) {
      updateCurrentTopic(topicId);
    }
  });

  const initialTopic = window.location.hash.slice(1);
  if (initialTopic) {
    updateCurrentTopic(initialTopic);
  }

  setNavigationState(false);

  const helpVersion = document.querySelector('meta[name="help-version"]')?.content;
  document.querySelectorAll("[data-help-version]").forEach((element) => {
    element.textContent = helpVersion || "unversioned";
  });

  // Hover/focus tooltips for hard terms (e.g. <a class="term" href="#glossary-cat">CAT</a>).
  // The definition text is read live from the glossary's own <dt>/<dd> pair, not duplicated here --
  // a glossary entry that changes can't silently leave a second, stale copy elsewhere. The link
  // itself is a real, working fallback with no JavaScript and on touch devices with no hover.
  function initTermTooltips() {
    const termLinks = Array.from(document.querySelectorAll("a.term"));
    if (termLinks.length === 0) {
      return;
    }

    const tooltip = document.createElement("div");
    tooltip.className = "term-tooltip";
    tooltip.setAttribute("role", "tooltip");
    tooltip.hidden = true;
    document.body.appendChild(tooltip);

    let activeLink = null;

    function showTooltip(link) {
      const targetId = link.getAttribute("href")?.slice(1);
      const term = targetId && document.getElementById(targetId);
      const definition = term?.nextElementSibling;
      if (!term || !definition || definition.tagName !== "DD") {
        return;
      }

      activeLink = link;
      tooltip.textContent = definition.textContent.trim();
      tooltip.hidden = false;

      const linkRect = link.getBoundingClientRect();
      const tooltipRect = tooltip.getBoundingClientRect();
      const left = Math.min(Math.max(8, linkRect.left), window.innerWidth - tooltipRect.width - 8);
      tooltip.style.left = `${left + window.scrollX}px`;
      tooltip.style.top = `${linkRect.bottom + window.scrollY + 6}px`;
    }

    function hideTooltip(link) {
      if (activeLink !== link) {
        return;
      }
      tooltip.hidden = true;
      activeLink = null;
    }

    termLinks.forEach((link) => {
      link.addEventListener("mouseenter", () => showTooltip(link));
      link.addEventListener("mouseleave", () => hideTooltip(link));
      link.addEventListener("focus", () => showTooltip(link));
      link.addEventListener("blur", () => hideTooltip(link));
    });

    document.addEventListener(
      "keydown",
      (event) => {
        if (event.key === "Escape" && activeLink) {
          hideTooltip(activeLink);
        }
      },
      true
    );
  }

  initTermTooltips();

  main?.setAttribute("data-help-ready", "true");
})();
