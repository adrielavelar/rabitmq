FROM golang:1.25 as builder
RUN go install go.k6.io/xk6/cmd/xk6@latest \
 && /go/bin/xk6 build --with github.com/ptsypyshev/xk6-amqp-custom@latest -o /k6

FROM alpine:3.19
RUN apk add --no-cache ca-certificates && update-ca-certificates
COPY --from=builder /k6 /usr/local/bin/k6
ENTRYPOINT ["/usr/local/bin/k6"]
