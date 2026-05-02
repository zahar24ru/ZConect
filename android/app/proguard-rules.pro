# Keep kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class com.zconect.viewer.**$$serializer { *; }
-keepclassmembers class com.zconect.viewer.** { *** Companion; }
-keepclasseswithmembers class com.zconect.viewer.** { kotlinx.serialization.KSerializer serializer(...); }

# Keep WebRTC
-keep class org.webrtc.** { *; }

# OkHttp
-dontwarn okhttp3.**
-dontwarn okio.**
