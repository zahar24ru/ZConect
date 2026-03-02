FROM golang:1.22-alpine AS build
WORKDIR /src

RUN apk add --no-cache ca-certificates git

# Pre-fetch modules for reproducible builds (go.sum may be absent in repo).
COPY go.mod ./
RUN go mod tidy && go mod download

COPY . .
RUN go build -o /out/signaling ./cmd/signaling

FROM alpine:3.20
WORKDIR /app
COPY --from=build /out/signaling /app/signaling
EXPOSE 8080
CMD ["/app/signaling"]
