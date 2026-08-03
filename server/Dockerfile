# syntax=docker/dockerfile:1.7

FROM golang:1.26.2-alpine3.22 AS build

WORKDIR /src/server
ARG TARGETARCH=amd64
COPY server/go.mod server/go.sum ./
RUN go mod download
COPY server/cmd ./cmd
COPY server/internal ./internal
COPY server/migrations ./migrations
RUN CGO_ENABLED=0 GOOS=linux GOARCH=${TARGETARCH} go build \
    -trimpath \
    -ldflags="-s -w" \
    -o /out/termlink-server \
    ./cmd/termlink-server

FROM gcr.io/distroless/static-debian12:nonroot

COPY --from=build /out/termlink-server /termlink-server

USER 65532:65532
EXPOSE 8080

ENTRYPOINT ["/termlink-server"]
CMD ["serve"]
