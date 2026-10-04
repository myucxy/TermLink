package dev.termlink.product

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DeleteForever
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material.icons.filled.Terminal
import androidx.compose.material3.Button
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedIconButton
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.termlink.harness.R
import dev.termlink.model.TerminalCommandCapability
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun RemoteFileBrowserPage(
    state: RemoteFileBrowserState,
    searchSupported: Boolean,
    supportedCommands: List<TerminalCommandCapability>,
    favorites: List<RemoteDirectoryFavorite>,
    favoriteSupportedCommands: (RemoteDirectoryFavorite) -> List<TerminalCommandCapability>,
    onOpenDirectory: (String) -> Unit,
    onOpenFavorite: (RemoteDirectoryFavorite) -> Unit,
    onSearch: (String) -> Unit,
    onLoadMore: (String, String, Long) -> Unit,
    onDownload: (RemoteFileEntry) -> Unit,
    onDownloadFavorite: (RemoteDirectoryFavorite) -> Unit,
    onOpenShell: (String, TerminalCommandCapability) -> Unit,
    onOpenFavoriteShell: (RemoteDirectoryFavorite, TerminalCommandCapability) -> Unit,
    onOpenAiDirectory: (UUID, String) -> String?,
    onToggleFavorite: (RemoteFileEntry) -> Unit,
    onRemoveFavorite: (RemoteDirectoryFavorite) -> Unit,
    modifier: Modifier = Modifier,
    highlightedFile: String? = null,
    onReturnToAi: (() -> Unit)? = null,
) {
    var selectedTabIndex by rememberSaveable(state.pcId) { mutableIntStateOf(0) }
    var aiError by rememberSaveable(state.pcId) { mutableStateOf<String?>(null) }
    var searchQuery by rememberSaveable(state.pcId, state.currentPath) { mutableStateOf(state.query) }
    val parentTarget = remoteFileParentTarget(state.currentPath, state.parentPath)
    val directoryList = rememberLazyListState()
    val targetIndex = state.entries.indexOfFirst { it.path.equals(highlightedFile, ignoreCase = true) }
    androidx.compose.runtime.LaunchedEffect(highlightedFile, state.currentPath, targetIndex, selectedTabIndex) {
        if (highlightedFile != null && targetIndex >= 0 && selectedTabIndex == 0) directoryList.scrollToItem(targetIndex + 1)
    }
    val currentDirectory = remoteCurrentDirectoryEntry(state.currentPath)
    val currentDirectoryFavorite = currentDirectory != null && state.pcId?.let { pcId ->
        isRemoteDirectoryFavorite(favorites, pcId, currentDirectory.path)
    } == true
    Column(modifier = modifier.fillMaxSize()) {
        if (onReturnToAi != null) Row(Modifier.fillMaxWidth().padding(start = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(highlightedFile?.substringAfterLast('\\').orEmpty(), Modifier.weight(1f), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            TextButton(onClick = onReturnToAi) { Text(stringResource(R.string.ai_directory_return)) }
        }
        PrimaryTabRow(
            selectedTabIndex = selectedTabIndex,
            modifier = Modifier.height(44.dp),
        ) {
            Tab(
                selected = selectedTabIndex == 0,
                onClick = { selectedTabIndex = 0 },
                modifier = Modifier.fillMaxHeight(),
                text = {
                    Text(
                        stringResource(R.string.remote_directory_contents),
                        style = MaterialTheme.typography.labelLarge,
                    )
                },
            )
            Tab(
                selected = selectedTabIndex == 1,
                onClick = { selectedTabIndex = 1 },
                modifier = Modifier.fillMaxHeight(),
                text = {
                    Text(
                        stringResource(R.string.remote_favorites_title),
                        style = MaterialTheme.typography.labelLarge,
                    )
                },
            )
        }
        LazyColumn(
            state = directoryList,
            modifier = Modifier.fillMaxSize(),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(
                start = 16.dp,
                top = 8.dp,
                end = 16.dp,
                bottom = 14.dp,
            ),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (selectedTabIndex == 0) {
                item(key = "header") {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            Text(
                                state.pcName,
                                style = MaterialTheme.typography.titleSmall,
                                fontWeight = FontWeight.SemiBold,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                            Text(
                                state.currentPath.ifBlank { stringResource(R.string.remote_files_root) },
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.MiddleEllipsis,
                            )
                        }
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            OutlinedIconButton(
                                onClick = { parentTarget?.let(onOpenDirectory) },
                                enabled = parentTarget != null && !state.loading,
                                modifier = Modifier.size(40.dp),
                                shape = androidx.compose.foundation.shape.RoundedCornerShape(12.dp),
                            ) {
                                Icon(
                                    Icons.AutoMirrored.Filled.ArrowBack,
                                    contentDescription = stringResource(R.string.remote_files_parent),
                                    modifier = Modifier.size(20.dp),
                                )
                            }
                            if (searchSupported) {
                                RemoteDirectorySearchField(
                                    query = searchQuery,
                                    loading = state.loading,
                                    onQueryChange = { value ->
                                        if (isValidRemoteFileSearchQuery(value)) searchQuery = value
                                    },
                                    onSearch = onSearch,
                                    onClear = {
                                        searchQuery = ""
                                        if (state.query.isNotEmpty() || state.error != null) onSearch("")
                                    },
                                    modifier = Modifier.weight(1f),
                                )
                            } else {
                                Spacer(Modifier.weight(1f))
                            }
                            CurrentDirectoryActions(
                                directory = currentDirectory,
                                openingCommandId = state.openingCommandId,
                                supportedCommands = supportedCommands,
                                favorite = currentDirectoryFavorite,
                                onDownload = onDownload,
                                onOpenShell = onOpenShell,
                                onOpenAi = { path -> state.pcId?.let { aiError = onOpenAiDirectory(it, path) } },
                                onToggleFavorite = { directory -> onToggleFavorite(directory) },
                            )
                        }
                        state.error?.let { error ->
                            Text(error, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                        }
                        if (state.loading && state.entries.isEmpty()) {
                            Row(
                                modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
                                horizontalArrangement = Arrangement.Center,
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                CircularProgressIndicator(modifier = Modifier.size(24.dp), strokeWidth = 2.dp)
                                Spacer(Modifier.width(10.dp))
                                Text(stringResource(R.string.remote_files_loading))
                            }
                        } else if (!state.loading && state.entries.isEmpty() && state.error == null) {
                            Text(
                                stringResource(
                                    if (state.query.isEmpty()) R.string.remote_files_empty
                                    else R.string.remote_files_no_matches,
                                ),
                                modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
                items(state.entries, key = RemoteFileEntry::path) { entry ->
                    RemoteFileEntryRow(
                        entry = entry,
                        highlighted = entry.path.equals(highlightedFile, ignoreCase = true),
                        openingCommandId = state.openingCommandId,
                        supportedCommands = supportedCommands,
                        favorite = state.pcId?.let { pcId ->
                            isRemoteDirectoryFavorite(favorites, pcId, entry.path)
                        } == true,
                        onOpenDirectory = onOpenDirectory,
                        onDownload = onDownload,
                        onOpenShell = onOpenShell,
                        onOpenAi = { path -> state.pcId?.let { aiError = onOpenAiDirectory(it, path) } },
                        onToggleFavorite = { onToggleFavorite(entry) },
                    )
                }
                state.nextOffset?.let { offset ->
                    item(key = "more-$offset") {
                        Button(
                            onClick = { onLoadMore(state.currentPath, state.query, offset) },
                            enabled = !state.loading,
                            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
                        ) {
                            if (state.loading) {
                                CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                                Spacer(Modifier.width(8.dp))
                            }
                            Text(stringResource(R.string.remote_files_load_more))
                        }
                    }
                }
            } else if (favorites.isEmpty()) {
                item(key = "favorites-empty") {
                    Text(
                        stringResource(R.string.remote_favorites_empty),
                        modifier = Modifier.fillMaxWidth().padding(vertical = 28.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else {
                items(
                    items = favorites,
                    key = { favorite -> "favorite-${favorite.pcId}-${favorite.path.lowercase(Locale.ROOT)}" },
                ) { favorite ->
                    RemoteDirectoryFavoriteRow(
                        favorite = favorite,
                        openingCommandId = state.openingCommandId,
                        supportedCommands = favoriteSupportedCommands(favorite),
                        onOpen = {
                            selectedTabIndex = 0
                            onOpenFavorite(favorite)
                        },
                        onDownload = { onDownloadFavorite(favorite) },
                        onOpenShell = { shell -> onOpenFavoriteShell(favorite, shell) },
                        onOpenAi = { aiError = onOpenAiDirectory(favorite.pcId, favorite.path) },
                        onRemove = { onRemoveFavorite(favorite) },
                    )
                }
            }
        }
    }
    aiError?.let { message ->
        AlertDialog(onDismissRequest = { aiError = null },
            title = { Text(stringResource(R.string.action_new_ai_chat)) },
            text = { Text(message) },
            confirmButton = { TextButton(onClick = { aiError = null }) { Text(stringResource(android.R.string.ok)) } })
    }
}

@Composable
private fun DirectoryAiAction(onClick: () -> Unit) {
    DropdownMenuItem(text = { Text(stringResource(R.string.action_new_ai_chat)) },
        leadingIcon = { Icon(Icons.Filled.AutoAwesome, contentDescription = null) }, onClick = onClick)
}

@Composable
private fun RemoteDirectorySearchField(
    query: String,
    loading: Boolean,
    onQueryChange: (String) -> Unit,
    onSearch: (String) -> Unit,
    onClear: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val focusManager = LocalFocusManager.current
    val submit = {
        if (!loading) {
            onSearch(query)
            focusManager.clearFocus()
        }
    }
    Surface(
        modifier = modifier.height(40.dp),
        shape = androidx.compose.foundation.shape.RoundedCornerShape(12.dp),
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline),
    ) {
        Row(
            modifier = Modifier.fillMaxSize().padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(
                onClick = submit,
                enabled = !loading,
                modifier = Modifier.size(32.dp),
            ) {
                Icon(
                    Icons.Filled.Search,
                    contentDescription = stringResource(R.string.remote_files_search_action),
                    modifier = Modifier.size(18.dp),
                )
            }
            BasicTextField(
                value = query,
                onValueChange = onQueryChange,
                modifier = Modifier.weight(1f).fillMaxHeight(),
                singleLine = true,
                textStyle = MaterialTheme.typography.bodyMedium.copy(color = MaterialTheme.colorScheme.onSurface),
                cursorBrush = SolidColor(MaterialTheme.colorScheme.primary),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { submit() }),
                decorationBox = { innerTextField ->
                    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.CenterStart) {
                        if (query.isEmpty()) {
                            Text(
                                stringResource(R.string.remote_files_search_hint),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        innerTextField()
                    }
                },
            )
            if (query.isNotEmpty()) {
                IconButton(onClick = onClear, modifier = Modifier.size(32.dp)) {
                    Icon(
                        Icons.Filled.Close,
                        contentDescription = stringResource(R.string.remote_files_search_clear),
                        modifier = Modifier.size(18.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun CurrentDirectoryActions(
    directory: RemoteFileEntry?,
    openingCommandId: String?,
    supportedCommands: List<TerminalCommandCapability>,
    favorite: Boolean,
    onDownload: (RemoteFileEntry) -> Unit,
    onOpenShell: (String, TerminalCommandCapability) -> Unit,
    onOpenAi: (String) -> Unit,
    onToggleFavorite: (RemoteFileEntry) -> Unit,
) {
    var menuExpanded by remember(directory?.path) { mutableStateOf(false) }
    Box {
        OutlinedIconButton(
            onClick = { menuExpanded = true },
            enabled = directory != null,
            modifier = Modifier.size(40.dp),
            shape = androidx.compose.foundation.shape.RoundedCornerShape(12.dp),
        ) {
            Icon(
                Icons.Filled.MoreVert,
                contentDescription = stringResource(R.string.remote_current_directory_actions),
                modifier = Modifier.size(20.dp),
            )
        }
        DropdownMenu(expanded = menuExpanded, onDismissRequest = { menuExpanded = false }) {
            if (directory != null) {
                DirectoryAiAction { menuExpanded = false; onOpenAi(directory.path) }
                DropdownMenuItem(
                    text = { Text(stringResource(R.string.action_download_zip)) },
                    leadingIcon = { Icon(Icons.Filled.Download, contentDescription = null) },
                    onClick = {
                        menuExpanded = false
                        onDownload(directory)
                    },
                )
                supportedCommands.forEach { command ->
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.action_open_terminal_command, command.displayName)) },
                        leadingIcon = {
                            if (openingCommandId == command.id) {
                                CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                            } else {
                                Icon(Icons.Filled.Terminal, contentDescription = null)
                            }
                        },
                        enabled = openingCommandId == null,
                        onClick = {
                            menuExpanded = false
                            onOpenShell(directory.path, command)
                        },
                    )
                }
                if (supportedCommands.isEmpty()) {
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.remote_shells_unavailable)) },
                        leadingIcon = { Icon(Icons.Filled.Terminal, contentDescription = null) },
                        enabled = false,
                        onClick = {},
                    )
                }
                DropdownMenuItem(
                    text = {
                        Text(stringResource(
                            if (favorite) R.string.action_remove_favorite else R.string.action_add_favorite,
                        ))
                    },
                    leadingIcon = {
                        Icon(
                            if (favorite) Icons.Filled.Star else Icons.Filled.StarBorder,
                            contentDescription = null,
                        )
                    },
                    onClick = {
                        menuExpanded = false
                        onToggleFavorite(directory)
                    },
                )
            }
        }
    }
}

@Composable
private fun RemoteDirectoryFavoriteRow(
    favorite: RemoteDirectoryFavorite,
    openingCommandId: String?,
    supportedCommands: List<TerminalCommandCapability>,
    onOpen: () -> Unit,
    onDownload: () -> Unit,
    onOpenShell: (TerminalCommandCapability) -> Unit,
    onOpenAi: () -> Unit,
    onRemove: () -> Unit,
) {
    var menuExpanded by remember(favorite.pcId, favorite.path) { mutableStateOf(false) }
    Surface(
        modifier = Modifier.fillMaxWidth().clickable(onClick = onOpen),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.55f),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Icon(Icons.Filled.Star, contentDescription = null, tint = MaterialTheme.colorScheme.tertiary)
            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(
                    favorite.directoryName.ifBlank { favorite.path },
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    stringResource(R.string.remote_favorite_location, favorite.pcName, favorite.path),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Box {
                IconButton(onClick = { menuExpanded = true }, modifier = Modifier.size(48.dp)) {
                    Icon(Icons.Filled.MoreVert, contentDescription = stringResource(R.string.action_more_options))
                }
                DropdownMenu(expanded = menuExpanded, onDismissRequest = { menuExpanded = false }) {
                    DirectoryAiAction { menuExpanded = false; onOpenAi() }
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.action_download_zip)) },
                        leadingIcon = { Icon(Icons.Filled.Download, contentDescription = null) },
                        onClick = {
                            menuExpanded = false
                            onDownload()
                        },
                    )
                    supportedCommands.forEach { command ->
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.action_open_terminal_command, command.displayName)) },
                            leadingIcon = {
                                if (openingCommandId == command.id) {
                                    CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                                } else {
                                    Icon(Icons.Filled.Terminal, contentDescription = null)
                                }
                            },
                            enabled = openingCommandId == null,
                            onClick = {
                                menuExpanded = false
                                onOpenShell(command)
                            },
                        )
                    }
                    if (supportedCommands.isEmpty()) {
                        DropdownMenuItem(
                            text = { Text(stringResource(R.string.remote_shells_unavailable)) },
                            leadingIcon = { Icon(Icons.Filled.Terminal, contentDescription = null) },
                            enabled = false,
                            onClick = {},
                        )
                    }
                    DropdownMenuItem(
                        text = { Text(stringResource(R.string.action_delete_favorite)) },
                        leadingIcon = { Icon(Icons.Filled.DeleteForever, contentDescription = null) },
                        onClick = {
                            menuExpanded = false
                            onRemove()
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun RemoteFileEntryRow(
    entry: RemoteFileEntry,
    highlighted: Boolean = false,
    openingCommandId: String?,
    supportedCommands: List<TerminalCommandCapability>,
    favorite: Boolean,
    onOpenDirectory: (String) -> Unit,
    onDownload: (RemoteFileEntry) -> Unit,
    onOpenShell: (String, TerminalCommandCapability) -> Unit,
    onOpenAi: (String) -> Unit,
    onToggleFavorite: () -> Unit,
) {
    var menuExpanded by remember(entry.path) { mutableStateOf(false) }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = if (highlighted) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.45f)),
    ) {
        Column {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable(enabled = entry.kind == RemoteFileKind.DIRECTORY) {
                        onOpenDirectory(entry.path)
                    }
                    .padding(start = 12.dp, top = 10.dp, end = 4.dp, bottom = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Icon(
                    if (entry.kind == RemoteFileKind.DIRECTORY) Icons.Filled.Folder else Icons.Filled.Description,
                    contentDescription = null,
                    tint = if (entry.kind == RemoteFileKind.DIRECTORY) {
                        MaterialTheme.colorScheme.tertiary
                    } else {
                        MaterialTheme.colorScheme.primary
                    },
                )
                Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    Text(entry.name, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    val details = buildList {
                        entry.sizeBytes?.let { add(formatRemoteBytes(it)) }
                        entry.modifiedAt?.let {
                            add(REMOTE_FILE_TIME_FORMATTER.format(it.atZone(ZoneId.systemDefault())))
                        }
                    }.joinToString(" · ")
                    if (details.isNotEmpty()) {
                        Text(
                            details,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                Box {
                    IconButton(onClick = { menuExpanded = true }, modifier = Modifier.size(48.dp)) {
                        Icon(Icons.Filled.MoreVert, contentDescription = stringResource(R.string.action_more_options))
                    }
                    DropdownMenu(expanded = menuExpanded, onDismissRequest = { menuExpanded = false }) {
                        if (entry.kind == RemoteFileKind.DIRECTORY) {
                            DirectoryAiAction { menuExpanded = false; onOpenAi(entry.path) }
                        }
                        DropdownMenuItem(
                            text = {
                                Text(stringResource(
                                    if (entry.kind == RemoteFileKind.DIRECTORY) {
                                        R.string.action_download_zip
                                    } else {
                                        R.string.action_download
                                    },
                                ))
                            },
                            leadingIcon = { Icon(Icons.Filled.Download, contentDescription = null) },
                            onClick = {
                                menuExpanded = false
                                onDownload(entry)
                            },
                        )
                        if (entry.kind == RemoteFileKind.DIRECTORY) {
                            supportedCommands.forEach { command ->
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.action_open_terminal_command, command.displayName)) },
                                    leadingIcon = {
                                        if (openingCommandId == command.id) {
                                            CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                                        } else {
                                            Icon(Icons.Filled.Terminal, contentDescription = null)
                                        }
                                    },
                                    enabled = openingCommandId == null,
                                    onClick = {
                                        menuExpanded = false
                                        onOpenShell(entry.path, command)
                                    },
                                )
                            }
                            if (supportedCommands.isEmpty()) {
                                DropdownMenuItem(
                                    text = { Text(stringResource(R.string.remote_shells_unavailable)) },
                                    leadingIcon = { Icon(Icons.Filled.Terminal, contentDescription = null) },
                                    enabled = false,
                                    onClick = {},
                                )
                            }
                            DropdownMenuItem(
                                text = {
                                    Text(stringResource(
                                        if (favorite) R.string.action_remove_favorite else R.string.action_add_favorite,
                                    ))
                                },
                                leadingIcon = {
                                    Icon(
                                        if (favorite) Icons.Filled.Star else Icons.Filled.StarBorder,
                                        contentDescription = null,
                                    )
                                },
                                onClick = {
                                    menuExpanded = false
                                    onToggleFavorite()
                                },
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
internal fun RemoteDownloadCenterPage(
    pcId: UUID? = null,
    pcName: String? = null,
    records: List<RemoteDownloadRecord>,
    onCancel: (UUID) -> Unit,
    onRetry: (RemoteDownloadRecord) -> Unit,
    onDelete: (UUID) -> Unit,
    modifier: Modifier = Modifier,
) {
    val visibleRecords = if (pcId == null) records else records.filter { it.pcId == pcId }
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item(key = "download-header") {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                pcName?.takeIf(String::isNotBlank)?.let { name ->
                    Text(name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                }
                Text(
                    stringResource(R.string.remote_download_delete_note),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (visibleRecords.isEmpty()) {
                    Text(
                        stringResource(R.string.remote_download_empty),
                        modifier = Modifier.fillMaxWidth().padding(vertical = 28.dp),
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
        items(visibleRecords, key = RemoteDownloadRecord::id) { record ->
            RemoteDownloadRecordRow(record, onCancel, onRetry, onDelete)
        }
    }
}

@Composable
private fun RemoteDownloadRecordRow(
    record: RemoteDownloadRecord,
    onCancel: (UUID) -> Unit,
    onRetry: (RemoteDownloadRecord) -> Unit,
    onDelete: (UUID) -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = ProductShape,
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outline.copy(alpha = 0.45f)),
    ) {
        Column(modifier = Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Icon(
                    if (record.directoryZip) Icons.Filled.Folder else Icons.Filled.Description,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                )
                Column(modifier = Modifier.weight(1f)) {
                    Text(record.filename, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Text(
                        record.pcName,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        remoteDownloadStatusLabel(record),
                        style = MaterialTheme.typography.bodySmall,
                        color = when (record.status) {
                            RemoteDownloadStatus.COMPLETED -> MaterialTheme.colorScheme.secondary
                            RemoteDownloadStatus.FAILED -> MaterialTheme.colorScheme.error
                            else -> MaterialTheme.colorScheme.onSurfaceVariant
                        },
                    )
                }
            }
            if (record.status.isActive) {
                val total = record.totalBytes
                if (total != null && total > 0L) {
                    LinearProgressIndicator(
                        progress = { (record.downloadedBytes.toFloat() / total).coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth(),
                    )
                } else {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                }
            }
            val totalText = record.totalBytes?.let(::formatRemoteBytes) ?: "—"
            Text(
                stringResource(
                    R.string.remote_download_progress,
                    formatRemoteBytes(record.downloadedBytes),
                    totalText,
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                record.remotePath,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                stringResource(
                    R.string.remote_download_target,
                    record.targetUri,
                    if (record.directoryZip) stringResource(R.string.remote_download_zip) else "",
                ),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            record.error?.let {
                Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
            Text(
                REMOTE_FILE_TIME_FORMATTER.format(record.updatedAt.atZone(ZoneId.systemDefault())),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                if (record.status == RemoteDownloadStatus.COMPLETED) {
                    TextButton(onClick = { openDownloadedDocument(context, record, false) }) { Text(stringResource(R.string.action_open_download)) }
                    TextButton(onClick = { openDownloadedDocument(context, record, true) }) { Text(stringResource(R.string.action_share_download)) }
                }
                when {
                    record.status.isActive -> TextButton(
                        onClick = { onCancel(record.id) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.Cancel, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_cancel_download))
                    }
                    record.status == RemoteDownloadStatus.FAILED ||
                        record.status == RemoteDownloadStatus.CANCELLED -> TextButton(
                        onClick = { onRetry(record) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.Refresh, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_retry_download))
                    }
                }
                if (record.status.isTerminal) {
                    TextButton(
                        onClick = { onDelete(record.id) },
                        modifier = Modifier.heightIn(min = 48.dp),
                    ) {
                        Icon(Icons.Filled.DeleteForever, contentDescription = null)
                        Spacer(Modifier.width(6.dp))
                        Text(stringResource(R.string.action_delete_download_record))
                    }
                }
            }
        }
    }
}

@Composable
private fun remoteDownloadStatusLabel(record: RemoteDownloadRecord): String = when (record.status) {
    RemoteDownloadStatus.PREPARING -> stringResource(
        if (record.directoryZip) R.string.remote_download_compressing else R.string.remote_download_preparing,
    )
    RemoteDownloadStatus.RESUMING -> stringResource(R.string.remote_download_resuming)
    RemoteDownloadStatus.DOWNLOADING -> stringResource(R.string.remote_download_downloading)
    RemoteDownloadStatus.VERIFYING -> stringResource(R.string.remote_download_verifying)
    RemoteDownloadStatus.COMPLETED -> stringResource(R.string.remote_download_completed)
    RemoteDownloadStatus.FAILED -> stringResource(R.string.remote_download_failed)
    RemoteDownloadStatus.CANCELLED -> stringResource(R.string.remote_download_cancelled)
}

private fun formatRemoteBytes(bytes: Long): String = when {
    bytes >= 1024L * 1024L * 1024L -> String.format(Locale.getDefault(), "%.1f GB", bytes / (1024f * 1024f * 1024f))
    bytes >= 1024L * 1024L -> String.format(Locale.getDefault(), "%.1f MB", bytes / (1024f * 1024f))
    bytes >= 1024L -> String.format(Locale.getDefault(), "%.1f KB", bytes / 1024f)
    else -> "$bytes B"
}

private val REMOTE_FILE_TIME_FORMATTER: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm")
