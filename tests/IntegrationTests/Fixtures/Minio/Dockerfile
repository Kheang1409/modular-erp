# Disposable test fixture only; not part of the application image.
# MinIO source: https://github.com/minio/minio/commit/7aac2a2c5b7c882e68c1ce017d8256be2feea27f
# Upstream license: https://github.com/minio/minio/blob/7aac2a2c5b7c882e68c1ce017d8256be2feea27f/LICENSE
FROM golang:1.26.9-bookworm@sha256:d9c68c2c51161e12fd77e4c6320687c9cd86e1af1e3ad6e6cd63ff970641453c AS build
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
RUN go install -trimpath -ldflags="-s -w" github.com/minio/minio@v0.0.0-20260212201848-7aac2a2c5b7c

FROM alpine:3.24.2@sha256:294b683cb724975bec92580e1e685676bd4b50bda910ddb8c51d4cabeaec77e6 AS fixture
RUN addgroup -S -g 65532 minio \
    && adduser -S -u 65532 -G minio -h /data minio \
    && mkdir -p /data \
    && chown minio:minio /data
COPY --from=build /go/bin/minio /usr/local/bin/minio
ENV MINIO_UPDATE=off MINIO_BROWSER=off
EXPOSE 9000
USER minio:minio
HEALTHCHECK --interval=2s --timeout=2s --start-period=10s --retries=10 \
    CMD wget --quiet --spider http://127.0.0.1:9000/minio/health/ready || exit 1
ENTRYPOINT ["/usr/local/bin/minio"]
