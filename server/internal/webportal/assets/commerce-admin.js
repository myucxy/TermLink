(() => {
  "use strict";

  const codeNodes = [...document.querySelectorAll("[data-generated-code]")];
  if (codeNodes.length === 0) return;

  const codes = codeNodes.map((node) => node.textContent.trim()).filter(Boolean);
  const status = document.querySelector("[data-generated-code-status]");
  let acknowledged = false;

  const setStatus = (message) => {
    if (status) status.textContent = message;
  };

  const fallbackCopy = (value) => {
    const field = document.createElement("textarea");
    field.value = value;
    field.setAttribute("readonly", "");
    field.style.position = "fixed";
    field.style.opacity = "0";
    document.body.append(field);
    field.select();
    const copied = document.execCommand("copy");
    field.remove();
    return copied;
  };

  const copyText = async (value) => {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(value);
      return;
    }
    if (!fallbackCopy(value)) throw new Error("copy unavailable");
  };

  document.querySelectorAll("[data-copy-code]").forEach((button) => {
    button.addEventListener("click", async () => {
      const code = button.closest(".generated-code-row")?.querySelector("[data-generated-code]")?.textContent.trim();
      if (!code) return;
      try {
        await copyText(code);
        setStatus("已复制邀请码 / Invitation code copied");
      } catch (_) {
        setStatus("复制失败，请手动选择 / Copy failed; select the code manually");
      }
    });
  });

  document.querySelector("[data-copy-all-codes]")?.addEventListener("click", async () => {
    try {
      await copyText(`${codes.join("\r\n")}\r\n`);
      setStatus(`已复制 ${codes.length} 个邀请码 / ${codes.length} codes copied`);
    } catch (_) {
      setStatus("复制失败，请逐条复制 / Copy failed; copy each code individually");
    }
  });

  document.querySelectorAll("[data-download-codes]").forEach((button) => {
    button.addEventListener("click", () => {
      const format = button.dataset.downloadCodes === "csv" ? "csv" : "txt";
      const content = format === "csv"
        ? `invitation_code\r\n${codes.map((code) => `"${code}"`).join("\r\n")}\r\n`
        : `${codes.join("\r\n")}\r\n`;
      const blob = new Blob([content], {type: format === "csv" ? "text/csv;charset=utf-8" : "text/plain;charset=utf-8"});
      const link = document.createElement("a");
      const downloadURL = URL.createObjectURL(blob);
      link.href = downloadURL;
      link.download = `termlink-invitation-codes.${format}`;
      document.body.append(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(downloadURL);
      setStatus(`已下载 ${format.toUpperCase()} / ${format.toUpperCase()} downloaded`);
    });
  });

  document.querySelector("[data-generated-code-ack]")?.addEventListener("click", () => {
    acknowledged = true;
  });

  window.addEventListener("beforeunload", (event) => {
    if (acknowledged) return;
    event.preventDefault();
    event.returnValue = "";
  });
})();
