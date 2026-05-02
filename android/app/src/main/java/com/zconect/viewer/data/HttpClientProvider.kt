package com.zconect.viewer.data

import okhttp3.ConnectionPool
import okhttp3.Dispatcher
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/**
 * Shared OkHttpClient singleton — экономит memory + native threads.
 *
 * Background (audit 2026-04-27): App имел 4 independent OkHttpClient instances
 * (SessionApi, SignalingClient, PresenceService, TelemetryService). Каждый
 * client allocates own thread pool (Dispatcher) + connection pool. Множилось
 * количество thread'ов на rapid reconnects (new SessionApi() для каждого
 * navigation event).
 *
 * Solution: один shared client с **two specialized variants**:
 *   - [api] — для REST API calls (10s timeouts, OK для slow networks)
 *   - [websocket] — для WebSocket connections (no read timeout, ping every 20s)
 *
 * Variants share underlying connection pool через `.newBuilder()` — overriding
 * только specific timeouts. Это OkHttp recommended pattern.
 *
 * NOT thread-pool isolation issue: OkHttp's Dispatcher already pool-bounded
 * (64 max requests), shared instance fine.
 */
object HttpClientProvider {

    /**
     * Base client — shared connection pool, dispatcher, и thread pool. Все
     * variants ниже наследуют через `.newBuilder()`.
     */
    private val baseClient: OkHttpClient by lazy {
        OkHttpClient.Builder()
            // Connection pool: 5 idle connections, kept alive 5 min — стандартные
            // OkHttp defaults (хороший trade-off для mobile).
            .connectionPool(ConnectionPool(5, 5, TimeUnit.MINUTES))
            // Dispatcher: 64 max requests overall, 5 per host — defaults достаточно.
            .dispatcher(Dispatcher().apply {
                maxRequests = 32  // меньше чем default 64 — для mobile разумно
                maxRequestsPerHost = 8
            })
            .build()
    }

    /**
     * REST API client — short timeouts для responsive UI. Если call'у нужно
     * больше — pass explicit timeout через `Request.Builder().tag(...)` или
     * cancel coroutine на upper layer.
     */
    val api: OkHttpClient by lazy {
        baseClient.newBuilder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(10, TimeUnit.SECONDS)
            .writeTimeout(10, TimeUnit.SECONDS)
            .build()
    }

    /**
     * Telemetry/Presence client — slightly tighter timeouts (5s) potому что
     * этот endpoints fail-fast acceptable (если сервер не отвечает 5s — лучше
     * skip heartbeat чем держать пользователя ждать).
     */
    val telemetry: OkHttpClient by lazy {
        baseClient.newBuilder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(8, TimeUnit.SECONDS)
            .build()
    }

    /**
     * WebSocket client — no read timeout (long-lived), explicit ping каждые 20s
     * для NAT keepalive (matches server's pingInterval=15s — bidirectional
     * traffic каждые ~7-10s avg, NAT entries alive).
     */
    val websocket: OkHttpClient by lazy {
        baseClient.newBuilder()
            .readTimeout(0, TimeUnit.MILLISECONDS) // no timeout for long-lived WS
            .pingInterval(20, TimeUnit.SECONDS)
            .build()
    }
}
