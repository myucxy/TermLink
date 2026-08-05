ARG KEYCLOAK_BASE_IMAGE=quay.io/keycloak/keycloak:26.3.2@sha256:98fab020a3a490aba0978f237e2a06cd0ea42bf149c6cf10f11c0aaf27728ff2

FROM ${KEYCLOAK_BASE_IMAGE} AS builder
ENV KC_DB=dev-file \
    KC_HEALTH_ENABLED=true
COPY acceptance/keycloak-theme/termlink/ /opt/keycloak/themes/termlink-01b-v11/
RUN /opt/keycloak/bin/kc.sh build

FROM ${KEYCLOAK_BASE_IMAGE}
COPY --from=builder /opt/keycloak/ /opt/keycloak/
