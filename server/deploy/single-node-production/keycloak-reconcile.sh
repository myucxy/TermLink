#!/usr/bin/env bash
set -Eeuo pipefail

readonly KCADM=/opt/keycloak/bin/kcadm.sh
readonly KCADM_CONFIG=/tmp/kcadm.config
readonly REALM=termlink
readonly USER_PROFILE=/opt/termlink/keycloak-user-profile.json

die() {
  printf 'TermLink Keycloak initialization failed: %s\n' "$*" >&2
  exit 1
}

read_secret() {
  local path="$1"
  local value
  [[ -f "$path" ]] || die "missing secret $path"
  value="$(<"$path")"
  [[ -n "$value" ]] || die "empty secret $path"
  printf '%s' "$value"
}

kc() {
  "$KCADM" "$@" --config "$KCADM_CONFIG"
}

csv_id_by_name() {
  local expected="$1"
  local id
  local name
  while IFS=, read -r id name _; do
    if [[ "$name" == "$expected" ]]; then
      printf '%s' "$id"
      return 0
    fi
  done
}

execution_id_by_name() {
  local expected="$1"
  local id
  local name
  local level
  while IFS=, read -r id name level _; do
    if [[ "$name" == "$expected" && "$level" == 0 ]]; then
      printf '%s' "$id"
      return 0
    fi
  done
}

client_id() {
  local client="$1"
  kc get clients -r "$REALM" -q "clientId=$client" \
    --fields id --format csv --noquotes | tail -n 1
}

ensure_client() {
  local client="$1"
  local id
  id="$(client_id "$client")"
  if [[ -z "$id" ]]; then
    id="$(kc create clients -r "$REALM" -i \
      -s "clientId=$client" -s protocol=openid-connect)"
  fi
  [[ -n "$id" ]] || die "could not resolve client $client"
  printf '%s' "$id"
}

ensure_mapper() {
  local client_uuid="$1"
  local mapper_name="$2"
  local mapper_file="$3"
  local mapper_id
  mapper_id="$(kc get "clients/$client_uuid/protocol-mappers/models" -r "$REALM" \
    --fields id,name --format csv --noquotes | csv_id_by_name "$mapper_name")"
  if [[ -z "$mapper_id" ]]; then
    kc create "clients/$client_uuid/protocol-mappers/models" -r "$REALM" \
      -f "$mapper_file" >/dev/null
  fi
}

attach_default_scope() {
  local client_uuid="$1"
  local scope_name="$2"
  local scope_id
  scope_id="$(printf '%s\n' "$client_scope_table" | csv_id_by_name "$scope_name")"
  [[ -n "$scope_id" ]] || die "missing Keycloak client scope $scope_name"
  kc update "clients/$client_uuid/default-client-scopes/$scope_id" -r "$REALM" >/dev/null
}

bootstrap_password="$(read_secret /run/secrets/keycloak_bootstrap_admin_password)"
initial_admin_password="$(read_secret /run/secrets/initial_admin_password)"
recovery_client_secret="$(read_secret /run/secrets/keycloak_admin_client_secret)"
public_url="${TERMLINK_PUBLIC_URL:-}"
admin_email="${TERMLINK_INITIAL_ADMIN_EMAIL:-}"

[[ "$public_url" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$ ]] || die 'invalid TERMLINK_PUBLIC_URL'
[[ "$admin_email" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]] || \
  die 'invalid TERMLINK_INITIAL_ADMIN_EMAIL'

authenticated=false
for attempt in $(seq 1 90); do
  if "$KCADM" config credentials --config "$KCADM_CONFIG" \
    --server http://keycloak:8080 --realm master \
    --user termlink-bootstrap --password "$bootstrap_password" >/dev/null 2>&1; then
    authenticated=true
    break
  fi
  sleep 1
done
[[ "$authenticated" == true ]] || die 'Keycloak bootstrap administrator did not become available'
unset bootstrap_password

kc update "realms/$REALM" \
  -s displayName=TermLink \
  -s loginTheme=termlink-01b-v18 \
  -s accountTheme=termlink-01b-v18 \
  -s internationalizationEnabled=true \
  -s 'supportedLocales=["zh-CN","en"]' \
  -s defaultLocale=zh-CN \
  -s sslRequired=external \
  -s registrationAllowed=false \
  -s verifyEmail=false \
  -s resetPasswordAllowed=false \
  -s rememberMe=true \
  -s loginWithEmailAllowed=true \
  -s duplicateEmailsAllowed=false \
  -s editUsernameAllowed=false \
  -s bruteForceProtected=true \
  -s accessTokenLifespan=300 \
  -s accessTokenLifespanForImplicitFlow=300 \
  -s ssoSessionIdleTimeout=1800 \
  -s ssoSessionMaxLifespan=28800 \
  -s revokeRefreshToken=true \
  -s refreshTokenMaxReuse=0 >/dev/null

for role_name in SUPER_ADMIN ADMIN AUDITOR USER TERMLINK_BOOTSTRAP_MANAGED_V1; do
  if ! kc get "roles/$role_name" -r "$REALM" >/dev/null 2>&1; then
    kc create roles -r "$REALM" -s "name=$role_name" >/dev/null
  fi
done

flow_table="$(kc get authentication/flows -r "$REALM" \
  --fields id,alias --format csv --noquotes)"
native_flow_id="$(printf '%s\n' "$flow_table" | csv_id_by_name termlink-native-browser)"
if [[ -z "$native_flow_id" ]]; then
  native_flow_id="$(kc create authentication/flows -r "$REALM" -i \
    -s alias=termlink-native-browser -s providerId=basic-flow \
    -s topLevel=true -s builtIn=false)"
  kc create authentication/flows/termlink-native-browser/executions/execution \
    -r "$REALM" -s provider=auth-cookie >/dev/null
  kc create authentication/flows/termlink-native-browser/executions/flow \
    -r "$REALM" -s alias=termlink-native-forms -s type=basic-flow >/dev/null
  kc create authentication/flows/termlink-native-forms/executions/execution \
    -r "$REALM" -s provider=auth-username-password-form >/dev/null
fi

native_executions="$(kc get authentication/flows/termlink-native-browser/executions \
  -r "$REALM" --fields id,displayName,level --format csv --noquotes)"
native_cookie_id="$(printf '%s\n' "$native_executions" | \
  execution_id_by_name Cookie)"
native_forms_id="$(printf '%s\n' "$native_executions" | \
  execution_id_by_name termlink-native-forms)"
[[ -n "$native_flow_id" && -n "$native_cookie_id" && -n "$native_forms_id" ]] || \
  die 'native authentication flow is incomplete'
kc update authentication/flows/termlink-native-browser/executions -r "$REALM" -n \
  -s "id=$native_cookie_id" -s requirement=ALTERNATIVE >/dev/null
kc update authentication/flows/termlink-native-browser/executions -r "$REALM" -n \
  -s "id=$native_forms_id" -s requirement=ALTERNATIVE >/dev/null

flow_table="$(kc get authentication/flows -r "$REALM" \
  --fields id,alias --format csv --noquotes)"
step_up_flow_id="$(printf '%s\n' "$flow_table" | csv_id_by_name termlink-web-mfa-step-up)"
if [[ -z "$step_up_flow_id" ]]; then
  step_up_flow_id="$(kc create authentication/flows -r "$REALM" -i \
    -s alias=termlink-web-mfa-step-up -s providerId=basic-flow \
    -s topLevel=true -s builtIn=false \
    -s 'description=Same-account Web verification with an existing session and OTP.')"
  kc create authentication/flows/termlink-web-mfa-step-up/executions/execution \
    -r "$REALM" -s provider=auth-cookie >/dev/null
  kc create authentication/flows/termlink-web-mfa-step-up/executions/execution \
    -r "$REALM" -s provider=auth-otp-form >/dev/null
fi

step_up_executions="$(kc get authentication/flows/termlink-web-mfa-step-up/executions \
  -r "$REALM" --fields id,displayName,level --format csv --noquotes)"
step_up_cookie_id="$(printf '%s\n' "$step_up_executions" | \
  execution_id_by_name Cookie)"
step_up_otp_id="$(printf '%s\n' "$step_up_executions" | \
  execution_id_by_name 'OTP Form')"
[[ -n "$step_up_flow_id" && -n "$step_up_cookie_id" && -n "$step_up_otp_id" ]] || \
  die 'Web step-up authentication flow is incomplete'
kc update authentication/flows/termlink-web-mfa-step-up/executions -r "$REALM" -n \
  -s "id=$step_up_cookie_id" -s requirement=REQUIRED >/dev/null
kc update authentication/flows/termlink-web-mfa-step-up/executions -r "$REALM" -n \
  -s "id=$step_up_otp_id" -s requirement=REQUIRED >/dev/null

client_scope_table="$(kc get client-scopes -r "$REALM" \
  --fields id,name --format csv --noquotes)"
registration_scope_id="$(printf '%s\n' "$client_scope_table" | \
  csv_id_by_name termlink-registration-attempt)"
if [[ -z "$registration_scope_id" ]]; then
  registration_scope_id="$(kc create client-scopes -r "$REALM" -i \
    -s name=termlink-registration-attempt -s protocol=openid-connect)"
  client_scope_table="$(kc get client-scopes -r "$REALM" \
    --fields id,name --format csv --noquotes)"
fi
kc update "client-scopes/$registration_scope_id" -r "$REALM" \
  -s 'attributes={"include.in.token.scope":"false","display.on.consent.screen":"false"}' >/dev/null
[[ -f "$USER_PROFILE" ]] || die 'missing Keycloak user profile'
kc update "realms/$REALM/users/profile" -f "$USER_PROFILE" >/dev/null

server_client_id="$(ensure_client termlink-server)"
kc update "clients/$server_client_id" -r "$REALM" \
  -s enabled=true -s bearerOnly=true -s protocol=openid-connect >/dev/null

android_client_id="$(ensure_client termlink-android)"
kc update "clients/$android_client_id" -r "$REALM" \
  -s enabled=true -s publicClient=true -s bearerOnly=false \
  -s standardFlowEnabled=true -s directAccessGrantsEnabled=true \
  -s implicitFlowEnabled=false -s serviceAccountsEnabled=false \
  -s fullScopeAllowed=false -s "baseUrl=$public_url" \
  -s 'redirectUris=["dev.termlink:/oauth2redirect"]' -s 'webOrigins=[]' \
  -s 'attributes={"pkce.code.challenge.method":"S256"}' \
  -s "authenticationFlowBindingOverrides={\"browser\":\"$native_flow_id\"}" >/dev/null
kc delete "clients/$android_client_id/default-client-scopes/$registration_scope_id" \
  -r "$REALM" >/dev/null 2>&1 || true
kc update "clients/$android_client_id/optional-client-scopes/$registration_scope_id" \
  -r "$REALM" >/dev/null

windows_client_id="$(ensure_client termlink-windows)"
kc update "clients/$windows_client_id" -r "$REALM" \
  -s enabled=true -s publicClient=true -s bearerOnly=false \
  -s standardFlowEnabled=true -s directAccessGrantsEnabled=true \
  -s implicitFlowEnabled=false -s serviceAccountsEnabled=false \
  -s fullScopeAllowed=false -s "baseUrl=$public_url" \
  -s 'redirectUris=["http://127.0.0.1/oauth2/callback"]' -s 'webOrigins=[]' \
  -s 'attributes={"pkce.code.challenge.method":"S256"}' \
  -s "authenticationFlowBindingOverrides={\"browser\":\"$native_flow_id\"}" >/dev/null

web_client_id="$(ensure_client termlink-web)"
kc update "clients/$web_client_id" -r "$REALM" \
  -s 'name=TermLink Account' -s enabled=true -s publicClient=true -s bearerOnly=false \
  -s standardFlowEnabled=true -s directAccessGrantsEnabled=false \
  -s implicitFlowEnabled=false -s serviceAccountsEnabled=false \
  -s fullScopeAllowed=false -s "rootUrl=$public_url" -s "baseUrl=$public_url/account" \
  -s "redirectUris=[\"$public_url/auth/callback\",\"$public_url/account\"]" \
  -s "webOrigins=[\"$public_url\"]" \
  -s "attributes={\"pkce.code.challenge.method\":\"S256\",\"post.logout.redirect.uris\":\"$public_url/*\"}" >/dev/null

step_up_client_id="$(ensure_client termlink-web-step-up)"
kc update "clients/$step_up_client_id" -r "$REALM" \
  -s 'name=TermLink High-Risk MFA' -s enabled=true -s publicClient=true -s bearerOnly=false \
  -s standardFlowEnabled=true -s directAccessGrantsEnabled=false \
  -s implicitFlowEnabled=false -s serviceAccountsEnabled=false \
  -s fullScopeAllowed=false -s "rootUrl=$public_url" -s "baseUrl=$public_url/account" \
  -s "redirectUris=[\"$public_url/auth/callback\"]" -s "webOrigins=[\"$public_url\"]" \
  -s 'attributes={"pkce.code.challenge.method":"S256"}' \
  -s "authenticationFlowBindingOverrides={\"browser\":\"$step_up_flow_id\"}" >/dev/null

for scope_name in web-origins acr roles profile basic email; do
  attach_default_scope "$windows_client_id" "$scope_name"
  attach_default_scope "$web_client_id" "$scope_name"
done
for scope_name in web-origins acr profile basic email; do
  attach_default_scope "$step_up_client_id" "$scope_name"
done
offline_scope_id="$(printf '%s\n' "$client_scope_table" | csv_id_by_name offline_access)"
[[ -n "$offline_scope_id" ]] || die 'missing Keycloak offline_access scope'
kc update "clients/$windows_client_id/optional-client-scopes/$offline_scope_id" \
  -r "$REALM" >/dev/null

cat > /tmp/termlink-server-audience.json <<'JSON'
{
  "name": "termlink-server-audience",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-audience-mapper",
  "consentRequired": false,
  "config": {
    "included.client.audience": "termlink-server",
    "access.token.claim": "true",
    "id.token.claim": "false"
  }
}
JSON
cat > /tmp/termlink-windows-nickname.json <<'JSON'
{
  "name": "termlink-windows-nickname",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-usermodel-attribute-mapper",
  "consentRequired": false,
  "config": {
    "user.attribute": "nickname",
    "claim.name": "nickname",
    "jsonType.label": "String",
    "id.token.claim": "false",
    "access.token.claim": "true",
    "userinfo.token.claim": "false"
  }
}
JSON
cat > /tmp/termlink-web-realm-roles.json <<'JSON'
{
  "name": "termlink-web-realm-roles",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-usermodel-realm-role-mapper",
  "consentRequired": false,
  "config": {
    "multivalued": "true",
    "claim.name": "realm_access.roles",
    "jsonType.label": "String",
    "id.token.claim": "true",
    "access.token.claim": "true",
    "userinfo.token.claim": "true"
  }
}
JSON
cat > /tmp/termlink-web-nickname.json <<'JSON'
{
  "name": "termlink-web-nickname",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-usermodel-attribute-mapper",
  "consentRequired": false,
  "config": {
    "user.attribute": "nickname",
    "claim.name": "nickname",
    "jsonType.label": "String",
    "id.token.claim": "true",
    "access.token.claim": "true",
    "userinfo.token.claim": "true"
  }
}
JSON
cat > /tmp/termlink-web-amr.json <<'JSON'
{
  "name": "termlink-web-amr",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-hardcoded-claim-mapper",
  "consentRequired": false,
  "config": {
    "claim.name": "amr",
    "claim.value": "[\"pwd\"]",
    "jsonType.label": "JSON",
    "id.token.claim": "true",
    "access.token.claim": "true",
    "userinfo.token.claim": "false",
    "access.tokenResponse.claim": "false"
  }
}
JSON
cat > /tmp/termlink-web-step-up-amr.json <<'JSON'
{
  "name": "termlink-web-step-up-amr",
  "protocol": "openid-connect",
  "protocolMapper": "oidc-hardcoded-claim-mapper",
  "consentRequired": false,
  "config": {
    "claim.name": "amr",
    "claim.value": "[\"otp\"]",
    "jsonType.label": "JSON",
    "id.token.claim": "true",
    "access.token.claim": "true",
    "userinfo.token.claim": "false",
    "access.tokenResponse.claim": "false"
  }
}
JSON

ensure_mapper "$android_client_id" termlink-server-audience /tmp/termlink-server-audience.json
ensure_mapper "$windows_client_id" termlink-server-audience /tmp/termlink-server-audience.json
ensure_mapper "$windows_client_id" termlink-windows-nickname /tmp/termlink-windows-nickname.json
ensure_mapper "$web_client_id" termlink-web-realm-roles /tmp/termlink-web-realm-roles.json
ensure_mapper "$web_client_id" termlink-web-nickname /tmp/termlink-web-nickname.json
ensure_mapper "$web_client_id" termlink-web-amr /tmp/termlink-web-amr.json
ensure_mapper "$step_up_client_id" termlink-web-step-up-amr /tmp/termlink-web-step-up-amr.json

recovery_client_id="$(ensure_client termlink-registration-recovery)"
kc update "clients/$recovery_client_id" -r "$REALM" \
  -s 'name=TermLink Registration and Identity Recovery' \
  -s enabled=true -s publicClient=false -s bearerOnly=false \
  -s standardFlowEnabled=false -s implicitFlowEnabled=false \
  -s directAccessGrantsEnabled=false -s serviceAccountsEnabled=true \
  -s fullScopeAllowed=false -s clientAuthenticatorType=client-secret \
  -s "secret=$recovery_client_secret" >/dev/null
unset recovery_client_secret

recovery_service_account="$(kc get "clients/$recovery_client_id/service-account-user" \
  -r "$REALM" --fields id,username --format csv --noquotes | tail -n 1)"
IFS=, read -r recovery_user_id recovery_username <<<"$recovery_service_account"
[[ -n "$recovery_user_id" && -n "$recovery_username" ]] || \
  die 'recovery service account is missing'
realm_management_id="$(client_id realm-management)"
[[ -n "$realm_management_id" ]] || die 'realm-management client is missing'

assigned_recovery_roles="$(kc get \
  "users/$recovery_user_id/role-mappings/clients/$realm_management_id" \
  -r "$REALM" --fields name --format csv --noquotes)"
while IFS= read -r assigned_role; do
  case "$assigned_role" in
    ''|query-users|manage-users|manage-realm) ;;
    *)
      kc remove-roles -r "$REALM" --uusername "$recovery_username" \
        --cclientid realm-management --rolename "$assigned_role" >/dev/null
      ;;
  esac
done <<<"$assigned_recovery_roles"

for recovery_role in query-users manage-users manage-realm; do
  if ! printf '%s\n' "$assigned_recovery_roles" | grep -Fxq "$recovery_role"; then
    kc add-roles -r "$REALM" --uusername "$recovery_username" \
      --cclientid realm-management --rolename "$recovery_role" >/dev/null
  fi
  recovery_role_id="$(kc get "clients/$realm_management_id/roles/$recovery_role" \
    -r "$REALM" --fields id --format csv --noquotes | tail -n 1)"
  [[ -n "$recovery_role_id" ]] || die "missing realm-management role $recovery_role"
  printf '[{"id":"%s","name":"%s","clientRole":true,"containerId":"%s"}]' \
    "$recovery_role_id" "$recovery_role" "$realm_management_id" \
    > /tmp/termlink-recovery-scope-role.json
  kc create "clients/$recovery_client_id/scope-mappings/clients/$realm_management_id" \
    -r "$REALM" -f /tmp/termlink-recovery-scope-role.json >/dev/null
done

admin_user_id="$(kc get users -r "$REALM" -q exact=true -q username=admin \
  --fields id --format csv --noquotes | tail -n 1)"
if [[ -z "$admin_user_id" ]]; then
  admin_user_id="$(kc create users -r "$REALM" -i \
    -s username=admin -s enabled=true)"
  kc update "users/$admin_user_id" -r "$REALM" \
    -s 'attributes.nickname=["TermLink Administrator"]' \
    -s "email=$admin_email" -s emailVerified=true -s enabled=true >/dev/null
  kc set-password -r "$REALM" --userid "$admin_user_id" \
    --new-password "$initial_admin_password" --temporary >/dev/null
  kc add-roles -r "$REALM" --uid "$admin_user_id" \
    --rolename TERMLINK_BOOTSTRAP_MANAGED_V1 >/dev/null
else
  admin_roles="$(kc get "users/$admin_user_id/role-mappings/realm" -r "$REALM" \
    --fields name --format csv --noquotes)"
  if ! printf '%s\n' "$admin_roles" | grep -Fxq TERMLINK_BOOTSTRAP_MANAGED_V1; then
    die 'refusing to grant SUPER_ADMIN to an unmanaged existing admin account'
  fi
fi
unset initial_admin_password
kc add-roles -r "$REALM" --uid "$admin_user_id" --rolename SUPER_ADMIN >/dev/null

if kc get users -r "$REALM" -q username=acceptance-user \
  --fields id --format csv --noquotes | grep -q .; then
  die 'acceptance-user must not exist in a production realm'
fi
if kc get users -r "$REALM" -q username=acceptance-probe \
  --fields id --format csv --noquotes | grep -q .; then
  die 'acceptance-probe must not exist in a production realm'
fi

rm -f "$KCADM_CONFIG" /tmp/termlink-*.json
printf 'TermLink Keycloak reconciliation completed.\n'
