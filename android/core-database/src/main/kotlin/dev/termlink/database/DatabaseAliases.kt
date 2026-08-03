package dev.termlink.database

/** Stable names for callers which model rows as summary/index entities. */
typealias DeviceSummaryEntity = DeviceEntity
typealias PcSummaryEntity = PcEntity
typealias SessionSummaryEntity = SessionEntity
typealias EventChunkIndexEntity = EventChunkEntity
typealias SnapshotIndexEntity = SnapshotEntity
typealias AckCursor = AckCursorEntity
typealias PreferenceEntity = HistoryPreferenceEntity

typealias AppDatabase = TermLinkDatabase
typealias HistoryDatabase = TermLinkDatabase
