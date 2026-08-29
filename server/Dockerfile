# syntax=docker/dockerfile:1.7@sha256:a57df69d0ea827fb7266491f2813635de6f17269be881f696fbfdf2d83dda33e

ARG SOURCE_DATE_EPOCH=1
FROM --platform=$BUILDPLATFORM golang:1.26.2-alpine3.22@sha256:7ef941168f213aa115df2e61364d67682129e99dc8188b734139dea862cc7d31 AS build

WORKDIR /src/server
ENV GOTOOLCHAIN=local
ARG SOURCE_DATE_EPOCH
ARG TARGETOS
ARG TARGETARCH
ARG VERSION
ARG REVISION
ARG CREATED
COPY server/go.mod server/go.sum ./
RUN go mod download && go mod verify
COPY server/cmd ./cmd
COPY server/internal ./internal
COPY server/migrations ./migrations
RUN CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} go build \
    -mod=readonly \
    -buildvcs=false \
    -trimpath \
    -ldflags="-s -w -X github.com/termlink/termlink/server/internal/buildinfo.injectedVersion=${VERSION} -X github.com/termlink/termlink/server/internal/buildinfo.injectedRevision=${REVISION} -X github.com/termlink/termlink/server/internal/buildinfo.injectedCreated=${CREATED}" \
    -o /out/termlink-server \
    ./cmd/termlink-server && \
    test -n "${SOURCE_DATE_EPOCH}" && \
    case "${SOURCE_DATE_EPOCH}" in *[!0-9]*) exit 1;; esac && \
    touch -d "@${SOURCE_DATE_EPOCH}" /out/termlink-server

FROM gcr.io/distroless/static-debian12:nonroot@sha256:f5b485ea962d9bd1186b2f6b3a061191539b905b82ec395de78cbfae51f20e35

ARG SOURCE_DATE_EPOCH
ARG VERSION=dev
ARG REVISION=unknown
ARG CREATED=unknown
ARG SOURCE=https://github.com/termlink/termlink

LABEL org.opencontainers.image.title="TermLink Server" \
      org.opencontainers.image.description="TermLink authenticated opaque realtime relay and API" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}" \
      org.opencontainers.image.source="${SOURCE}" \
      org.opencontainers.image.licenses="Apache-2.0"

COPY --from=build /out/termlink-server /termlink-server

USER 65532:65532
EXPOSE 8080 9090

ENTRYPOINT ["/termlink-server"]
CMD ["serve"]
