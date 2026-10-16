// Shared script for the bundled feedback forms. FeedbackFormLauncher inlines it into each page's
// temp copy, so the page stays one self-contained file. It must never contain a closing script tag.
(function () {
  "use strict";

  // The web3forms access key is stored XOR-masked so it cannot be read or grepped from the file.
  // This stops a casual read, not a determined one: the browser must send the real key.
  var MASK = [
    0x5a, 0x13, 0x7e, 0x22, 0x91, 0x4c, 0x08, 0xd3, 0x6b, 0x2f, 0xa4, 0x19,
    0xc7, 0x55, 0x3e, 0x80, 0x0d, 0xb2, 0x47, 0xe9, 0x71, 0x26, 0x9c, 0x5f,
    0x14, 0xab, 0x63, 0xf0, 0x38, 0x8e, 0x02, 0xd5, 0x4a, 0x7b, 0xc1, 0x29
  ];

  var KEY_BLOB = "ayYbEKV6OLdGGJcto3gKsWzUaogVQKVyJ5kBlQ64N+d9SqMc";

  function unmask(blob) {
    var bytes = atob(blob);
    var out = "";
    for (var i = 0; i < bytes.length; i++) {
      out += String.fromCharCode(bytes.charCodeAt(i) ^ MASK[i % MASK.length]);
    }
    return out;
  }

  // Node test hook for check-feedback.mjs; absent in a browser.
  if (typeof module !== "undefined" && module.exports) {
    module.exports = { unmask: unmask, KEY_BLOB: KEY_BLOB };
    return;
  }

  var config = window.ScanlineFeedback;
  var form = document.getElementById("form");
  var submit = document.getElementById("submit");
  var result = document.getElementById("result");
  var contact = document.getElementById("contact");
  var emailBlock = document.getElementById("emailBlock");
  var email = document.getElementById("email");

  // A disabled input is left out of the submission, so an unticked contact box sends no email.
  function syncContact() {
    var on = contact.checked;
    emailBlock.hidden = !on;
    email.disabled = !on;
    email.required = on;
  }
  contact.addEventListener("change", syncContact);
  syncContact();

  function show(text, cls) {
    result.textContent = text;
    result.className = cls || "";
  }

  form.addEventListener("submit", async function (event) {
    event.preventDefault();
    if (!form.reportValidity()) {
      return;
    }

    submit.disabled = true;
    show(result.dataset.sending);

    var data = new FormData(form);
    data.append("access_key", unmask(KEY_BLOB));
    if (config.subjectField) {
      var subjectValue = (data.get(config.subjectField) || "").toString().trim();
      if (subjectValue) {
        data.set("subject", config.subjectPrefix + subjectValue);
      }
    }

    var response;
    try {
      response = await fetch("https://api.web3forms.com/submit", { method: "POST", body: data });
    } catch (e) {
      show(result.dataset.offline, "err");
      submit.disabled = false;
      return;
    }

    try {
      var json = await response.json();
      if (json.success) {
        show(result.dataset.ok, "ok");
        form.reset();
        syncContact();
        return;
      }
      show(result.dataset.error + " " + (json.message || response.status), "err");
    } catch (e) {
      // Reached the server but the reply was not JSON (e.g. an HTML error page).
      show(result.dataset.error + " HTTP " + response.status, "err");
    }
    submit.disabled = false;
  });
})();
