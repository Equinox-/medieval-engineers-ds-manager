using System;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Meds.Shared
{
    public static class CustomLogFormat
    {
        private static readonly JsonEncodedText CategoryNameText = JsonEncodedText.Encode(nameof(LogInfo.CategoryName));
        private static readonly JsonEncodedText TimestampText = JsonEncodedText.Encode(nameof(LogInfo.Timestamp));
        private static readonly JsonEncodedText LogLevelText = JsonEncodedText.Encode(nameof(LogInfo.LogLevel));
        private static readonly JsonEncodedText EventIdText = JsonEncodedText.Encode(nameof(LogInfo.EventId));
        private static readonly JsonEncodedText EventIdNameText = JsonEncodedText.Encode("EventIdName");
        private static readonly JsonEncodedText ExceptionText = JsonEncodedText.Encode(nameof(LogInfo.Exception));
        private static readonly JsonEncodedText NameText = JsonEncodedText.Encode("Name");
        private static readonly JsonEncodedText MessageText = JsonEncodedText.Encode("Message");
        private static readonly JsonEncodedText StackTraceText = JsonEncodedText.Encode("StackTrace");
        private static readonly JsonEncodedText InnerExceptionText = JsonEncodedText.Encode("InnerException");
        private static readonly JsonEncodedText Trace = JsonEncodedText.Encode(nameof(LogLevel.Trace));
        private static readonly JsonEncodedText Debug = JsonEncodedText.Encode(nameof(LogLevel.Debug));
        private static readonly JsonEncodedText Information = JsonEncodedText.Encode(nameof(LogLevel.Information));
        private static readonly JsonEncodedText Warning = JsonEncodedText.Encode(nameof(LogLevel.Warning));
        private static readonly JsonEncodedText Error = JsonEncodedText.Encode(nameof(LogLevel.Error));
        private static readonly JsonEncodedText Critical = JsonEncodedText.Encode(nameof(LogLevel.Critical));
        private static readonly JsonEncodedText None = JsonEncodedText.Encode(nameof(LogLevel.None));

        public static void FormatHeader(Utf8JsonWriter writer, LogInfo info)
        {
            writer.WriteString(CategoryNameText, info.CategoryName);
            writer.WriteString(LogLevelText, LogLevelToEncodedText(info.LogLevel));
            if (info.EventId != default)
            {
                writer.WriteNumber(EventIdText, info.EventId.Id);
                writer.WriteString(EventIdNameText, info.EventId.Name);
            }

            writer.WriteString(TimestampText, info.Timestamp);
            if (info.Exception != null)
            {
                writer.WritePropertyName(ExceptionText);
                WriteException(writer, info.Exception);
            }
        }

        private static void WriteException(Utf8JsonWriter writer, Exception ex)
        {
            writer.WriteStartObject();
            writer.WriteString(NameText, ex.GetType().FullName);
            writer.WriteString(MessageText, ex.Message);
            writer.WriteString(StackTraceText, ex.StackTrace);
            if (ex.InnerException != null)
            {
                writer.WritePropertyName(InnerExceptionText);
                WriteException(writer, ex.InnerException);
            }

            writer.WriteEndObject();
        }

        private static JsonEncodedText LogLevelToEncodedText(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => Trace,
                LogLevel.Debug => Debug,
                LogLevel.Information => Information,
                LogLevel.Warning => Warning,
                LogLevel.Error => Error,
                LogLevel.Critical => Critical,
                LogLevel.None => None,
                _ => JsonEncodedText.Encode(((int)logLevel).ToString())
            };
        }
    }
}