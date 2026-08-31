(() => {
  "use strict";
  const form = document.getElementById("registration-form");
  if (!form) return;
  const continuationURL = form.dataset.registrationContinue || "/login";
  const humanVerificationRequired = form.dataset.humanVerificationRequired === "true";
  const humanContainer = document.getElementById("human-verification");
  const humanWrapper = humanContainer instanceof HTMLElement ? humanContainer.closest(".registration-human-verification") : null;
  const humanStatus = document.getElementById("human-verification-status");
  const message = document.getElementById("registration-message");
  const submit = form.querySelector('button[type="submit"]');
  const prefersChinese = (navigator.languages || [navigator.language || "en"])
    .some((value) => String(value).toLowerCase().startsWith("zh"));
  const copy = (prefersChinese ? {
    REGISTRATION_CLOSED: "注册已暂停。",
    INVITATION_REQUIRED: "请输入邀请码。",
    INVITATION_INVALID: "邀请码无效或当前不可使用。",
    REGISTRATION_RATE_LIMITED: "操作过于频繁，请稍后再试。",
    EMAIL_CODE_RATE_LIMITED: "验证码发送过于频繁，请稍后再试。",
    EMAIL_SERVICE_UNAVAILABLE: "邮件服务暂时不可用，请稍后重试。",
    EMAIL_CODE_INVALID: "邮箱验证码无效或已过期。",
    INVALID_REGISTRATION: "请检查用户名、昵称、邮箱和密码格式。",
    REGISTRATION_REJECTED: "账号未能创建。用户名或邮箱可能已被使用。",
    REGISTRATION_UNAVAILABLE: "注册服务暂时不可用，请稍后重试。",
    HUMAN_VERIFICATION_REQUIRED: "请先完成人机验证。",
    HUMAN_VERIFICATION_FAILED: "人机验证未通过，请重新验证。",
    HUMAN_VERIFICATION_UNAVAILABLE: "人机验证服务暂时不可用，请稍后重试。",
    humanVerificationLoading: "正在加载安全验证…",
    humanVerificationLoadFailed: "安全验证加载失败，请刷新页面后重试。",
    humanVerificationExpired: "人机验证已过期，请重新验证。",
    emailSending: "正在发送…",
    emailSendFailed: "验证码发送失败，请稍后重试。",
    emailSent: "验证码已发送，10 分钟内有效。",
    resendAfter: (seconds) => `${seconds} 秒后重发`,
    resend: "重新发送",
    sendCode: "发送验证码",
    passwordMismatch: "两次输入的密码不一致。",
    creating: "正在创建…",
    createFailed: "账号创建失败，请稍后重试。",
    created: "账号已创建，正在前往登录…",
    createAccount: "创建账号"
  } : {
    REGISTRATION_CLOSED: "Registration is currently paused.",
    INVITATION_REQUIRED: "Enter an invitation code.",
    INVITATION_INVALID: "The invitation code is invalid or unavailable.",
    REGISTRATION_RATE_LIMITED: "Too many attempts. Try again later.",
    EMAIL_CODE_RATE_LIMITED: "Verification codes were requested too frequently. Try again later.",
    EMAIL_SERVICE_UNAVAILABLE: "The email service is temporarily unavailable. Try again later.",
    EMAIL_CODE_INVALID: "The email verification code is invalid or expired.",
    INVALID_REGISTRATION: "Check the username, nickname, email, and password formats.",
    REGISTRATION_REJECTED: "The account could not be created. The username or email may already be in use.",
    REGISTRATION_UNAVAILABLE: "Registration is temporarily unavailable. Try again later.",
    HUMAN_VERIFICATION_REQUIRED: "Complete the human verification first.",
    HUMAN_VERIFICATION_FAILED: "Human verification failed. Complete it again.",
    HUMAN_VERIFICATION_UNAVAILABLE: "Human verification is temporarily unavailable. Try again later.",
    humanVerificationLoading: "Loading secure verification…",
    humanVerificationLoadFailed: "Secure verification could not load. Refresh the page and try again.",
    humanVerificationExpired: "Human verification expired. Complete it again.",
    emailSending: "Sending…",
    emailSendFailed: "The verification code could not be sent. Try again later.",
    emailSent: "Verification code sent. It is valid for 10 minutes.",
    resendAfter: (seconds) => `Resend in ${seconds}s`,
    resend: "Resend",
    sendCode: "Send verification code",
    passwordMismatch: "The passwords do not match.",
    creating: "Creating…",
    createFailed: "The account could not be created. Try again later.",
    created: "Account created. Redirecting to sign in…",
    createAccount: "Create account"
  });

  class PublicRegistrationError extends Error {
    constructor(publicMessage) {
      super("public registration failure");
      this.publicMessage = publicMessage;
    }
  };

  function showMessage(text, state = "") {
    message.textContent = text;
    if (state) message.dataset.state = state;
    else delete message.dataset.state;
  }

  function responseMessageKey(result) {
    if (!result || typeof result !== "object") return "";
    if (typeof result.code === "string") return result.code;
    if (typeof result.message_key === "string") return result.message_key;
    if (result.error && typeof result.error === "object") {
      if (typeof result.error.code === "string") return result.error.code;
      if (typeof result.error.message_key === "string") return result.error.message_key;
    }
    return "";
  }

  let humanToken = "";
  let humanWidgetID = null;

  function setHumanState(state, text = "") {
    if (humanWrapper instanceof HTMLElement) {
      if (state) humanWrapper.dataset.state = state;
      else delete humanWrapper.dataset.state;
    }
    if (humanStatus instanceof HTMLElement) {
      humanStatus.textContent = text;
      humanStatus.hidden = text === "";
    }
  }

  function hasHumanToken() {
    if (!humanVerificationRequired) return true;
    if (humanToken) return true;
    showMessage(copy.HUMAN_VERIFICATION_REQUIRED, "error");
    if (humanWrapper instanceof HTMLElement) humanWrapper.focus({preventScroll: false});
    return false;
  }

  function resetHumanVerification() {
    if (!humanVerificationRequired) return;
    humanToken = "";
    if (humanWidgetID !== null && window.turnstile && typeof window.turnstile.reset === "function") {
      window.turnstile.reset(humanWidgetID);
    }
  }

  function initializeHumanVerification() {
    if (!humanVerificationRequired) return;
    if (!(humanContainer instanceof HTMLElement) || !window.turnstile || typeof window.turnstile.render !== "function") {
      setHumanState("error", copy.humanVerificationLoadFailed);
      showMessage(copy.humanVerificationLoadFailed, "error");
      return;
    }
    const siteKey = humanContainer.dataset.siteKey || "";
    const action = humanContainer.dataset.action || "registration";
    if (!siteKey) {
      setHumanState("error", copy.humanVerificationLoadFailed);
      showMessage(copy.humanVerificationLoadFailed, "error");
      return;
    }
    setHumanState("loading", copy.humanVerificationLoading);
    try {
      humanWidgetID = window.turnstile.render(humanContainer, {
        sitekey: siteKey,
        action,
        callback: (token) => {
          humanToken = String(token || "");
          setHumanState(humanToken ? "ready" : "error");
          if (humanToken && message.dataset.state === "error" &&
              message.textContent === copy.HUMAN_VERIFICATION_REQUIRED) showMessage("");
        },
        "expired-callback": () => {
          humanToken = "";
          setHumanState("expired");
          showMessage(copy.humanVerificationExpired, "error");
        },
        "error-callback": () => {
          humanToken = "";
          setHumanState("error", copy.humanVerificationLoadFailed);
          showMessage(copy.HUMAN_VERIFICATION_FAILED, "error");
          return true;
        }
      });
    } catch (_) {
      humanWidgetID = null;
      setHumanState("error", copy.humanVerificationLoadFailed);
      showMessage(copy.humanVerificationLoadFailed, "error");
    }
  }

  initializeHumanVerification();

  const codeButton = document.getElementById("send-email-code");
  if (codeButton) {
    codeButton.addEventListener("click", async () => {
      const emailInput = form.elements.namedItem("email");
      const verificationInput = form.elements.namedItem("email_verification_id");
      if (!(emailInput instanceof HTMLInputElement) || !emailInput.reportValidity()) return;
      if (!hasHumanToken()) return;
      codeButton.disabled = true;
      codeButton.textContent = copy.emailSending;
      try {
        const response = await fetch("/v1/auth/register/email-code", {
          method: "POST", credentials: "same-origin",
          headers: {"Content-Type": "application/json", "Accept": "application/json"},
          body: JSON.stringify({email: emailInput.value, human_verification_token: humanToken})
        });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) {
          const key = responseMessageKey(result);
          throw new PublicRegistrationError(copy[key] || copy.emailSendFailed);
        }
        if (verificationInput instanceof HTMLInputElement) verificationInput.value = result.verification_id || "";
        showMessage(copy.emailSent, "success");
        let remaining = 60;
        const timer = window.setInterval(() => {
          remaining -= 1;
          codeButton.textContent = remaining > 0 ? copy.resendAfter(remaining) : copy.resend;
          if (remaining <= 0) {
            window.clearInterval(timer);
            codeButton.disabled = false;
          }
        }, 1000);
      } catch (error) {
        codeButton.disabled = false;
        codeButton.textContent = copy.sendCode;
        showMessage(error instanceof PublicRegistrationError ? error.publicMessage : copy.emailSendFailed, "error");
      } finally {
        resetHumanVerification();
      }
    });
  }

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    if (!form.reportValidity()) return;
    const values = new FormData(form);
    const payload = Object.fromEntries(values.entries());
    if (payload.password !== payload.password_confirm) {
      showMessage(copy.passwordMismatch, "error");
      return;
    }
    if (!hasHumanToken()) return;
    payload.human_verification_token = humanToken;
    submit.disabled = true;
    submit.textContent = copy.creating;
    showMessage("");
    try {
      const response = await fetch("/v1/auth/register", {
        method: "POST",
        credentials: "same-origin",
        headers: {"Content-Type": "application/json", "Accept": "application/json"},
        body: JSON.stringify(payload)
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) {
        const key = responseMessageKey(result);
        throw new PublicRegistrationError(copy[key] || copy.createFailed);
      }
      showMessage(copy.created, "success");
      window.location.assign(continuationURL);
    } catch (error) {
      showMessage(error instanceof PublicRegistrationError ? error.publicMessage : copy.createFailed, "error");
    } finally {
      resetHumanVerification();
      submit.disabled = false;
      submit.textContent = copy.createAccount;
    }
  });
})();
