import { Publisher, Consumer } from 'k6/x/amqp'

export let options = {
  vus: Number(__ENV.VUS || 32),
  duration: __ENV.DURATION || '30s',
}

const url = `amqp://${__ENV.RABBITMQ_USER || 'guest'}:${__ENV.RABBITMQ_PASS || 'guest'}@${__ENV.RABBITMQ_HOST || 'rabbitmq'}:${__ENV.RABBITMQ_PORT || 5672}/`
const exchangeName = __ENV.EXCHANGE || 'k6.exchange'
const queueName = __ENV.QUEUE || 'k6.queue'
const routingKey = __ENV.ROUTING_KEY || 'k6.key'
const exchangeKind = __ENV.EXCHANGE_KIND || 'direct'

const exchange = { name: exchangeName, kind: exchangeKind, durable: true }
const queue = { name: queueName, routing_key: routingKey, durable: true }

const publisher = new Publisher({ connection_url: url, exchange, queue })
const consumer = new Consumer({ connection_url: url, exchange, queue })

export default function () {
  publisher.publish({
    exchange: exchangeName,
    routing_key: routingKey,
    content_type: 'application/json',
    body: JSON.stringify({ ts: Date.now(), payload: 'bulk' }),
  })
}

export function teardown() {
  const limit = Number(__ENV.CONSUME_LIMIT || 1000)
  consumer.consume({ read_timeout: '5s', consume_limit: limit })
}

export function handleSummary(data) {
  const m = (name) => data.metrics?.[name] || {}
  const v = (metric, key) => (metric.values && metric.values[key] !== undefined ? metric.values[key] : '-')
  const it = m('iterations')
  const pub = m('publisher_message_count')
  const cons = m('consumer_message_count')
  const dur = m('iteration_duration')
  const html = `<!doctype html><html><head><meta charset="utf-8"><title>K6 RabbitMQ Report</title><style>body{font-family:system-ui,Arial,sans-serif;padding:20px;background:#f7f7f9;color:#222}h1{margin:0 0 12px}table{border-collapse:collapse;width:100%;background:#fff}th,td{border:1px solid #ddd;padding:8px;text-align:left}th{background:#f0f0f3}code{background:#eee;padding:2px 6px;border-radius:4px}</style></head><body><h1>K6 RabbitMQ Report</h1><p>Exchange <code>${exchangeName}</code>, queue <code>${queueName}</code>, routing key <code>${routingKey}</code>, kind <code>${exchangeKind}</code>.</p><table><tr><th>Métrica</th><th>Valor</th></tr><tr><td>Iterations (count)</td><td>${v(it,'count')}</td></tr><tr><td>Iterations (rate/s)</td><td>${(v(it,'rate') !== '-' ? Number(v(it,'rate')).toFixed(2) : '-')}</td></tr><tr><td>Publisher messages (count)</td><td>${v(pub,'count')}</td></tr><tr><td>Publisher rate (/s)</td><td>${(v(pub,'rate') !== '-' ? Number(v(pub,'rate')).toFixed(2) : '-')}</td></tr><tr><td>Consumer messages (count)</td><td>${v(cons,'count')}</td></tr><tr><td>Consumer rate (/s)</td><td>${(v(cons,'rate') !== '-' ? Number(v(cons,'rate')).toFixed(2) : '-')}</td></tr><tr><td>Iteration duration p95 (ms)</td><td>${(v(dur,'p(95)') !== '-' ? Number(v(dur,'p(95)')).toFixed(2) : '-')}</td></tr><tr><td>Iteration duration avg (ms)</td><td>${(v(dur,'avg') !== '-' ? Number(v(dur,'avg')).toFixed(6) : '-')}</td></tr></table></body></html>`
  return {
    '/reports/summary.json': JSON.stringify(data),
    '/reports/k6-summary.html': html,
  }
}