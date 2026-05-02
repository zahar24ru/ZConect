package com.zconect.viewer

import android.app.Application
import com.zconect.viewer.data.TelemetryService

class ZConectApp : Application() {

    private lateinit var telemetry: TelemetryService

    override fun onCreate() {
        super.onCreate()
        // Start anonymous telemetry heartbeat in background.
        // Safe to call here — it's non-blocking and errors are swallowed.
        telemetry = TelemetryService(this)
        telemetry.start()
    }
}
