(() => {
  "use strict";
  const form = document.getElementById("registration-form");
  if (!form) return;
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

  const codeButton = document.getElementById("send-email-code");
  if (codeButton) {
    codeButton.addEventListener("click", async () => {
      const emailInput = form.elements.namedItem("email");
      const verificationInput = form.elements.namedItem("email_verification_id");
      if (!(emailInput instanceof HTMLInputElement) || !emailInput.reportValidity()) return;
      codeButton.disabled = true;
      codeButton.textContent = copy.emailSending;
      try {
        const response = await fetch("/v1/auth/register/email-code", {
          method: "POST", credentials: "same-origin",
          headers: {"Content-Type": "application/json", "Accept": "application/json"},
          body: JSON.stringify({email: emailInput.value})
        });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) {
          const key = result.message_key || result.code || "";
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
        const key = result.message_key || result.code || "";
        throw new PublicRegistrationError(copy[key] || copy.createFailed);
      }
      showMessage(copy.created, "success");
      window.location.assign("/login?return_to=/account");
    } catch (error) {
      showMessage(error instanceof PublicRegistrationError ? error.publicMessage : copy.createFailed, "error");
    } finally {
      submit.disabled = false;
      submit.textContent = copy.createAccount;
    }
  });
})();
