FROM golang:1.24-alpine AS build

WORKDIR /src
COPY go.mod ./
COPY main.go main_test.go ./
RUN CGO_ENABLED=0 go test ./... \
    && CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/cinepersona-plex-relay .

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/cinepersona-plex-relay /cinepersona-plex-relay
EXPOSE 8080
ENTRYPOINT ["/cinepersona-plex-relay"]
