# syntax=docker/dockerfile:1.7

FROM golang:1.26.2-alpine3.22 AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY cmd ./cmd
COPY internal ./internal
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/termlink-e2e-server ./cmd/termlink-e2e-server

FROM alpine:3.22
LABEL org.opencontainers.image.title="TermLink E2E technical-validation relay" \
      org.opencontainers.image.description="Non-production, in-memory TermLink protocol validation relay"
RUN addgroup -S -g 65532 termlink && \
    adduser -S -D -H -u 65532 -G termlink termlink
COPY --from=build /out/termlink-e2e-server /termlink-e2e-server
USER 65532:65532
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=3s --retries=12 \
    CMD wget -q -T 2 -O /dev/null http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["/termlink-e2e-server"]
CMD ["serve"]
