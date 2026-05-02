FROM golang:1.25-alpine AS build
WORKDIR /src

RUN apk add --no-cache ca-certificates git

COPY . .
RUN go mod tidy && go mod download
RUN go build -o /out/signaling ./cmd/signaling

FROM alpine:3.21
RUN apk upgrade --no-cache
WORKDIR /app
COPY --from=build /out/signaling /app/signaling
EXPOSE 8080
CMD ["/app/signaling"]
