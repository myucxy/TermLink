<#macro registrationLayout bodyClass="" displayInfo=false displayMessage=true displayRequiredFields=false>
<!doctype html>
<html lang="${lang}"<#if realm.internationalizationEnabled> dir="${(locale.rtl)?then('rtl','ltr')}"</#if>>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="robots" content="noindex, nofollow">
  <meta name="color-scheme" content="light dark">
  <title>${msg("loginTitle", (realm.displayName!"TermLink"))}</title>
  <link rel="icon" href="${url.resourcesPath}/img/termlink-favicon.svg" type="image/svg+xml">
  <#if properties.styles?has_content>
    <#list properties.styles?split(' ') as style>
      <link href="${url.resourcesPath}/${style}" rel="stylesheet">
    </#list>
  </#if>
  <#if properties.scripts?has_content>
    <#list properties.scripts?split(' ') as script>
      <script src="${url.resourcesPath}/${script}" defer></script>
    </#list>
  </#if>
  <#if scripts??>
    <#list scripts as script>
      <script src="${script}" defer></script>
    </#list>
  </#if>
</head>
<body class="tl-page ${bodyClass}<#if pageId != 'login'> tl-page-action</#if>" data-page-id="login-${pageId}">
  <header class="identity-titlebar">
    <a class="identity-brand" href="/" aria-label="TermLink ${msg('termlinkHome')}">
      <img src="${url.resourcesPath}/img/termlink-mark.svg" alt="">
      <span><strong>TermLink</strong><small>Account Center</small></span>
    </a>
    <div class="identity-title-actions">
      <span class="identity-secure-origin"><span class="lock-icon" aria-hidden="true"></span>${msg('termlinkVerifiedOrigin')}</span>
      <button class="theme-toggle" type="button" data-theme-toggle aria-label="${msg('termlinkThemeToggle')}" title="${msg('termlinkThemeToggle')}" aria-pressed="false">
        <span class="theme-toggle-icon" aria-hidden="true">◐</span>
        <span class="theme-toggle-label">${msg('termlinkTheme')}</span>
      </button>
      <#if realm.internationalizationEnabled && locale.supported?size gt 1>
        <#assign englishLocaleUrl = "">
        <#assign chineseLocaleUrl = "">
        <#list locale.supported as l>
          <#if l.label?contains("English")>
            <#assign englishLocaleUrl = l.url>
          <#else>
            <#assign chineseLocaleUrl = l.url>
          </#if>
        </#list>
        <nav class="identity-languages" aria-label="${msg('languages')}">
          <a href="${chineseLocaleUrl}" aria-label="中文（简体）"<#if !locale.current?contains("English")> aria-current="page"</#if>>简体中文</a>
          <span aria-hidden="true">/</span>
          <a href="${englishLocaleUrl}" aria-label="English"<#if locale.current?contains("English")> aria-current="page"</#if>>EN</a>
        </nav>
      </#if>
    </div>
  </header>

  <main class="identity-layout">
    <section class="identity-context" aria-labelledby="identity-context-title">
      <div class="identity-context-copy">
        <p class="overline">SELF-HOSTED IDENTITY</p>
        <h1 id="identity-context-title">${msg("termlinkContextTitle")}</h1>
        <p>${msg("termlinkContextBody")}</p>
      </div>

      <dl class="identity-trust-list">
        <div>
          <span class="trust-icon shield-icon" aria-hidden="true"></span>
          <dt>${msg("termlinkIdentityService")}</dt>
          <dd>Keycloak · Healthy</dd>
        </div>
        <div>
          <span class="trust-icon users-icon" aria-hidden="true"></span>
          <dt>${msg("termlinkRegistrationPolicy")}</dt>
          <dd><#if realm.registrationAllowed>${msg("termlinkOpenRegistration")}<#else>${msg("termlinkInviteRegistration")}</#if></dd>
        </div>
        <div>
          <span class="trust-icon lock-icon" aria-hidden="true"></span>
          <dt>${msg("termlinkAuthorizationProtocol")}</dt>
          <dd>Authorization Code + PKCE</dd>
        </div>
      </dl>

      <div class="bootstrap-summary">
        <span class="bootstrap-icon shield-icon" aria-hidden="true"></span>
        <span><strong>${msg("termlinkFirstDockerStart")}</strong><small>${msg("termlinkBootstrapDetail")}</small></span>
        <a class="bootstrap-link" href="/#deploy">${msg("termlinkBootstrapGuide")}</a>
      </div>
    </section>

    <section class="identity-auth-pane" aria-labelledby="kc-page-title">
      <div class="identity-form-shell">
        <header class="identity-form-heading">
          <p class="overline">TERMLINK ACCOUNT</p>
          <h2 id="kc-page-title"><#nested "header"></h2>
          <p><#if pageId == "login">${msg("termlinkLoginLead")}<#else>${msg("termlinkAccountActionLead")}</#if></p>
        </header>

        <#if auth?has_content && auth.showUsername() && !auth.showResetCredentials()>
          <#nested "show-username">
          <div id="kc-username" class="attempted-user">
            <span>${auth.attemptedUsername}</span>
            <a id="reset-login" href="${url.loginRestartFlowUrl}" aria-label="${msg('restartLoginTooltip')}">${msg("restartLoginTooltip")}</a>
          </div>
        </#if>

        <#if displayMessage && message?has_content && (message.type != "warning" || !isAppInitiatedAction??)>
          <div class="tl-alert tl-alert-${message.type}" role="alert">
            <span class="tl-alert-mark" aria-hidden="true"></span>
            <span class="tl-alert-title">${kcSanitize(message.summary)?no_esc}</span>
          </div>
        </#if>

        <div id="kc-content">
          <#nested "form">

          <#if auth?has_content && auth.showTryAnotherWayLink()>
            <form id="kc-select-try-another-way-form" action="${url.loginAction}" method="post" class="try-another-form">
              <input type="hidden" name="tryAnotherWay" value="on">
              <button class="tl-button tl-button-secondary tl-button-block" type="submit">${msg("doTryAnotherWay")}</button>
            </form>
          </#if>

          <#nested "socialProviders">

          <#if displayInfo>
            <div id="kc-info" class="identity-info"><#nested "info"></div>
          </#if>
        </div>

        <p class="identity-footnote"><span class="lock-icon" aria-hidden="true"></span><span>${msg("termlinkOriginNotice")}</span></p>
      </div>
    </section>
  </main>
</body>
</html>
</#macro>
