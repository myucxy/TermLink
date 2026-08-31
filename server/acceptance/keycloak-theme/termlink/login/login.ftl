<#import "template.ftl" as layout>
<#import "passkeys.ftl" as passkeys>
<@layout.registrationLayout displayMessage=!messagesPerField.existsError('username','password') displayInfo=false; section>
  <#if section == "header">
    ${msg("loginAccountTitle")}
  <#elseif section == "form">
    <div class="auth-mode-switch" role="tablist" aria-label="${msg('termlinkAccountMode')}">
      <a class="is-active" href="${url.loginUrl}" role="tab" aria-selected="true">${msg("doLogIn")}</a>
      <#if layout.termlinkRegisterUrl?has_content>
        <a href="${layout.termlinkRegisterUrl}" role="tab" aria-selected="false">${msg("termlinkCreateAccount")}</a>
      </#if>
    </div>

    <#if realm.password>
      <form id="kc-form-login" class="tl-form" onsubmit="login.disabled = true; return true;" action="${url.loginAction}" method="post">
        <#if !usernameHidden??>
          <div class="tl-form-group">
            <label for="username" class="tl-label"><#if !realm.loginWithEmailAllowed>${msg("username")}<#elseif !realm.registrationEmailAsUsername>${msg("usernameOrEmail")}<#else>${msg("email")}</#if></label>
            <div class="tl-input-group tl-input-user">
              <span class="field-icon users-icon" aria-hidden="true"></span>
              <input tabindex="1" id="username" class="tl-input" name="username" value="${(login.username!'')}" type="text" autofocus autocomplete="${(enableWebAuthnConditionalUI?has_content)?then('username webauthn', 'username')}" aria-invalid="<#if messagesPerField.existsError('username','password')>true<#else>false</#if>" dir="ltr">
            </div>
          </div>
        </#if>

        <div class="tl-form-group">
          <label for="password" class="tl-label">${msg("password")}</label>
          <div class="tl-input-group tl-input-password" dir="ltr">
            <span class="field-icon lock-icon" aria-hidden="true"></span>
            <input tabindex="2" id="password" class="tl-input" name="password" type="password" autocomplete="current-password" aria-invalid="<#if messagesPerField.existsError('username','password')>true<#else>false</#if>">
            <button class="tl-password-toggle" type="button" aria-label="${msg('showPassword')}" aria-controls="password" data-password-toggle tabindex="3" data-icon-show="tl-password-icon-show" data-icon-hide="tl-password-icon-hide" data-label-show="${msg('showPassword')}" data-label-hide="${msg('hidePassword')}"><span aria-hidden="true"></span></button>
          </div>
          <#if messagesPerField.existsError('username','password')>
            <span id="input-error" class="tl-input-error" aria-live="polite">${kcSanitize(messagesPerField.getFirstError('username','password'))?no_esc}</span>
          </#if>
        </div>

        <div class="identity-form-options">
          <#if realm.rememberMe && !usernameHidden??>
            <label class="tl-check" for="rememberMe">
              <input tabindex="4" id="rememberMe" name="rememberMe" type="checkbox"<#if login.rememberMe??> checked</#if>>
              <span>${msg("rememberMe")}</span>
            </label>
          <#else>
            <span></span>
          </#if>
          <#if realm.resetPasswordAllowed>
            <a tabindex="5" class="text-action" href="${url.loginResetCredentialsUrl}">${msg("doForgotPassword")}</a>
          </#if>
        </div>

        <input type="hidden" id="id-hidden-input" name="credentialId"<#if auth.selectedCredential?has_content> value="${auth.selectedCredential}"</#if>>
        <button tabindex="6" class="tl-button tl-button-primary tl-button-block identity-submit" name="login" id="kc-login" type="submit"><span>${msg("doLogIn")}</span><span aria-hidden="true">›</span></button>
      </form>
    </#if>

    <@passkeys.conditionalUIData />
    <script type="module" src="${url.resourcesPath}/js/passwordVisibility.js"></script>
  <#elseif section == "socialProviders">
    <#if realm.password && social?? && social.providers?has_content>
      <div id="kc-social-providers" class="tl-social">
        <p>${msg("identity-provider-login-label")}</p>
        <ul class="tl-social-list">
          <#list social.providers as provider>
            <li><a data-once-link id="social-${provider.alias}" class="tl-button tl-button-secondary tl-button-block" href="${provider.loginUrl}">${provider.displayName!}</a></li>
          </#list>
        </ul>
      </div>
    </#if>
  </#if>
</@layout.registrationLayout>
