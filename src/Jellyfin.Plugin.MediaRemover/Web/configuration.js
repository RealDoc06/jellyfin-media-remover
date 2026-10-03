/* Jellyfin loads this embedded configuration page as an ES module. */
export default function (view) {
    'use strict';

    const element = (id) => view.querySelector('#' + id);
    const pageSize = 25;
    const reviewDialog = element('mrReviewDialog');
    const retryDialog = element('mrRetryDialog');
    let settings = null;
    let settingsTask = null;
    let settingsBusy = false;
    let settingsDirty = false;
    let startIndex = 0;
    let totalCount = 0;
    let listVersion = 0;
    let detailVersion = 0;
    let detailRequestsVersion = 0;
    let historyVersion = 0;
    let votesVersion = 0;
    let votes = null;
    let votesError = false;
    let detail = null;
    let preview = null;
    let expiryTimer = null;
    let removalBusy = false;
    let removalOptionsReady = false;
    let requesterQueue = [];
    let requesterActive = 0;
    let retryOperation = null;
    let retryBusy = false;
    let destroyed = false;

    // The custom API uses camelCase; accepting PascalCase also supports older server serializers.
    function normalize(value) {
        if (Array.isArray(value)) return value.map(normalize);
        if (value && typeof value === 'object') {
            return Object.fromEntries(Object.entries(value).map(([key, child]) => [key[0].toLowerCase() + key.slice(1), normalize(child)]));
        }
        return value;
    }

    async function request(path, method = 'GET', body, query) {
        const options = {
            type: method,
            url: window.ApiClient.getUrl('MediaRemover/' + path, query),
            dataType: 'json'
        };
        if (body !== undefined) {
            options.contentType = 'application/json';
            options.data = JSON.stringify(body);
        }
        try {
            return normalize(await window.ApiClient.ajax(options));
        } catch (response) {
            let message = '';
            try {
                const payload = typeof response?.json === 'function'
                    ? await response.json()
                    : response?.responseJSON || JSON.parse(response?.responseText || '{}');
                message = payload.message || payload.Message || '';
            } catch (_) { /* A proxy or interrupted connection may return no JSON body. */ }
            if (!message && response?.status === 401) message = 'Your Jellyfin session expired. Sign in again.';
            if (!message && response?.status === 403) message = 'Only Jellyfin administrators can use Media Remover.';
            throw new Error(message || 'The request failed. Check the server connection and try again.');
        }
    }

    function status(id, message = '', kind = '') {
        const target = element(id);
        target.textContent = message;
        target.className = 'mr-status' + (kind ? ' mr-' + kind : '') + (kind === 'error' || kind === 'warning' ? ' infoBanner' : '');
        target.setAttribute('role', kind === 'error' ? 'alert' : 'status');
    }

    function textNode(tag, text, className) {
        const node = document.createElement(tag);
        node.textContent = text;
        if (className) node.className = className;
        return node;
    }

    function cell(row, value, className) {
        const node = textNode('td', value, 'detailTableBodyCell' + (className ? ' ' + className : ''));
        row.classList.add('listItem-border');
        row.append(node);
        return node;
    }

    function button(label, action, style = 'button-link mr-link') {
        const node = textNode('button', label, 'emby-button ' + style);
        node.type = 'button';
        node.addEventListener('click', action);
        return node;
    }

    function addDefinition(target, label, value) {
        target.append(textNode('dt', label, 'secondaryText'), textNode('dd', value));
    }

    function formatDate(value) {
        if (!value) return 'Never';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? 'Unknown' : date.toLocaleString();
    }

    function statusLabel(value) {
        return { watched: 'Watched', inProgress: 'In progress', unwatched: 'Unwatched', empty: 'No available media', unlinked: 'No linked Jellyfin user' }[value] || 'Unknown';
    }

    function renderNames(target, users, emptyText, format) {
        target.replaceChildren();
        if (!users.length) {
            target.append(textNode('span', emptyText, 'secondaryText'));
            return;
        }
        users.forEach((user) => target.append(textNode('div', format(user))));
    }

    // Only stable provider/Jellyfin identifiers can link requester progress; never infer it from names.
    function renderRequesters(target, value, showProgress = false) {
        target.replaceChildren();
        if (value.state !== 'available') {
            const labels = {
                notRequested: 'No requests',
                notConfigured: 'Seerr not configured',
                missingMetadata: 'Missing TMDB ID',
                unavailable: 'Requests unavailable'
            };
            const message = labels[value.state] || 'Requests unavailable';
            const node = textNode('span', message, value.state === 'unavailable' ? 'mr-warning' : 'secondaryText');
            if (value.message) node.title = value.message;
            target.append(node);
            if (showProgress && value.message && value.message !== message) {
                target.append(textNode('div', value.message, 'secondaryText mr-note'));
            }
            return;
        }
        value.requesters.forEach((requester) => {
            const node = textNode('div', requester.name);
            if (showProgress) {
                let progress = statusLabel(requester.watchStatus);
                if (requester.watchedCount !== null && requester.episodeCount !== null && requester.watchStatus !== 'unlinked') {
                    progress += ' (' + requester.watchedCount + ' / ' + requester.episodeCount + ')';
                }
                node.append(textNode('span', ' · ' + progress, requester.watchStatus === 'watched' ? 'mr-success' : 'secondaryText'));
            }
            target.append(node);
        });
        if (value.unknownRequesterCount) {
            target.append(textNode('div', 'Unknown requester (' + value.unknownRequesterCount + ' request' + (value.unknownRequesterCount === 1 ? '' : 's') + ')', 'mr-warning'));
        }
        if (!target.children.length) target.append(textNode('span', 'Requester unavailable', 'mr-warning'));
    }

    function drainRequesterQueue() {
        while (requesterActive < 4 && requesterQueue.length) {
            const work = requesterQueue.shift();
            if (work.version !== listVersion || destroyed) continue;
            requesterActive++;
            request('Items/' + encodeURIComponent(work.id) + '/Requests').then((value) => {
                if (work.version === listVersion && !destroyed) renderRequesters(work.target, value);
            }).catch((error) => {
                if (work.version === listVersion && !destroyed) {
                    renderRequesters(work.target, { state: 'unavailable', message: error.message });
                }
            }).finally(() => {
                requesterActive--;
                drainRequesterQueue();
            });
        }
    }

    async function loadDetailRequests(itemId, version) {
        const requestVersion = ++detailRequestsVersion;
        element('mrRequestSummary').textContent = 'Loading requests…';
        try {
            const value = await request('Items/' + encodeURIComponent(itemId) + '/Requests');
            if (version !== detailVersion || requestVersion !== detailRequestsVersion || destroyed || !reviewDialog.open) return;
            renderRequesters(element('mrRequestSummary'), value, true);
        } catch (error) {
            if (version !== detailVersion || requestVersion !== detailRequestsVersion || destroyed || !reviewDialog.open) return;
            renderRequesters(element('mrRequestSummary'), { state: 'unavailable', message: error.message }, true);
        }
    }

    function progressCell(row, item, episodeCount) {
        const target = cell(row, item.watchedCount + ' / ' + episodeCount);
        if (item.inProgressCount) target.append(textNode('div', item.inProgressCount + ' in progress', 'secondaryText mr-note'));
    }

    function renderDetailVotes() {
        if (!detail) return;
        const target = element('mrDetailVotes');
        target.replaceChildren();
        if (!votes) {
            target.textContent = votesError ? 'Votes unavailable. Refresh votes to try again.' : 'Loading votes…';
            return;
        }
        const summary = votes.get(detail.id);
        renderNames(target, summary?.voters || [], 'No votes yet.', (voter) => voter.name + ' · ' + formatDate(voter.votedAt));
    }

    function updateVoteCounts() {
        view.querySelectorAll('[data-mr-votes]').forEach((target) => {
            const summary = votes?.get(target.dataset.mrVotes);
            target.textContent = votes ? String(summary?.voteCount || 0) : votesError ? 'Unavailable' : '…';
            target.title = summary ? summary.voters.map((voter) => voter.name).join(', ') : '';
        });
        renderDetailVotes();
    }

    function mediaLink(label, itemId) {
        const link = textNode('a', label, 'emby-button button-link mr-link');
        link.href = '#/details?id=' + encodeURIComponent(itemId) + '&serverId=' + encodeURIComponent(window.ApiClient.serverId());
        return link;
    }

    async function loadVotes() {
        const version = ++votesVersion;
        element('mrRefreshVotes').disabled = true;
        status('mrVotesStatus', 'Loading votes…');
        try {
            const result = await request('Voting/Items/Summary');
            if (version !== votesVersion || destroyed) return;
            votes = new Map(result.items.map((item) => [item.itemId, item]));
            votesError = false;
            const target = element('mrVoteRows');
            target.replaceChildren();
            result.items.sort((a, b) => b.voteCount - a.voteCount || a.name.localeCompare(b.name)).forEach((item) => {
                const row = document.createElement('tr');
                const title = cell(row, '');
                const hasManager = item.type === 'Series' || item.type === 'Movie';
                title.append(hasManager ? button(item.name, () => openReview(item.itemId)) : mediaLink(item.name, item.itemId));
                if (item.productionYear) title.append(textNode('span', item.productionYear, 'mr-year secondaryText'));
                const type = { Audio: 'Track', MusicAlbum: 'Album', AudioBook: 'Audiobook', BoxSet: 'Collection' }[item.type]
                    || item.type.replace(/([a-z])([A-Z])/g, '$1 $2');
                title.append(textNode('div', type + (item.context ? ' · ' + item.context : ''), 'secondaryText mr-note'));
                cell(row, item.voteCount);
                renderNames(cell(row, '', 'mr-names'), item.voters, 'No votes', (voter) => voter.name + ' · ' + formatDate(voter.votedAt));
                const actions = textNode('div', '', 'mr-actions');
                if (hasManager) {
                    actions.append(button('View', () => openReview(item.itemId), 'button-flat'),
                        button('Delete', () => openReview(item.itemId, true), 'raised button-delete'));
                } else {
                    actions.append(mediaLink('Open in Jellyfin', item.itemId));
                    actions.title = 'Provider removal is available for movies and series.';
                }
                cell(row, '').append(actions);
                target.append(row);
            });
            element('mrVotesCaption').textContent = result.totalVotes + ' vote' + (result.totalVotes === 1 ? '' : 's') + ' across ' + result.totalItems + ' items · Most votes first';
            status('mrVotesStatus', result.items.length ? '' : 'No votes yet. Users can vote from media menus, Home, or Deletion votes in the sidebar.');
        } catch (error) {
            if (version !== votesVersion || destroyed) return;
            votes = null;
            votesError = true;
            element('mrVoteRows').replaceChildren();
            element('mrVotesCaption').textContent = 'Votes unavailable';
            status('mrVotesStatus', error.message, 'error');
        } finally {
            if (version === votesVersion && !destroyed) {
                element('mrRefreshVotes').disabled = false;
                updateVoteCounts();
            }
        }
    }

    function setSettingsBusy(busy) {
        settingsBusy = busy;
        element('mrSonarrSettings').disabled = busy || !settings;
        element('mrRadarrSettings').disabled = busy || !settings;
        element('mrSeerrSettings').disabled = busy || !settings;
        element('mrSaveSettings').disabled = busy || !settings;
        element('mrTestConnections').disabled = busy || !settings || settingsDirty;
        element('mrReloadSettings').disabled = busy;
    }

    function populateSettings(value) {
        settings = value;
        settingsDirty = false;
        for (const provider of ['Sonarr', 'Radarr', 'Seerr']) {
            element('mr' + provider + 'Url').value = value[provider.toLowerCase() + 'Url'] || '';
            element('mr' + provider + 'Key').value = '';
            element('mrClear' + provider).checked = false;
            element('mr' + provider + 'Key').disabled = false;
            element('mr' + provider + 'KeyStatus').textContent = value['has' + provider + 'ApiKey'] ? 'A key is saved. Leave blank to keep it.' : 'No API key saved.';
        }
        if (!value.hasSonarrApiKey && !value.hasRadarrApiKey && !value.hasSeerrApiKey) element('mrConnectionsSection').open = true;
    }

    function refreshAfterSettings() {
        loadItems();
        if (detail && reviewDialog.open && !removalBusy) {
            invalidatePreview();
            applyRemovalDefaults();
            loadDetailRequests(detail.id, detailVersion);
        }
    }

    function loadSettings() {
        if (settingsTask) return settingsTask;
        if (settingsBusy) return Promise.resolve();
        const refreshing = Boolean(settings);
        settingsTask = (async () => {
            setSettingsBusy(true);
            status('mrSettingsStatus', 'Loading connections…');
            try {
                const value = await request('Settings');
                if (destroyed) return;
                populateSettings(value);
                status('mrSettingsStatus');
                if (refreshing) refreshAfterSettings();
            } catch (error) {
                status('mrSettingsStatus', error.message, 'error');
                element('mrConnectionsSection').open = true;
            } finally {
                settingsTask = null;
                setSettingsBusy(false);
            }
        })();
        return settingsTask;
    }

    async function saveSettings(event) {
        event.preventDefault();
        if (settingsBusy || !settings) return;
        setSettingsBusy(true);
        status('mrSettingsStatus', 'Saving connections…');
        element('mrConnectionResults').hidden = true;
        try {
            const result = await request('Settings', 'PUT', {
                sonarrUrl: element('mrSonarrUrl').value.trim(),
                radarrUrl: element('mrRadarrUrl').value.trim(),
                seerrUrl: element('mrSeerrUrl').value.trim(),
                sonarrApiKey: element('mrSonarrKey').value.trim() || null,
                radarrApiKey: element('mrRadarrKey').value.trim() || null,
                seerrApiKey: element('mrSeerrKey').value.trim() || null,
                clearSonarrApiKey: element('mrClearSonarr').checked,
                clearRadarrApiKey: element('mrClearRadarr').checked,
                clearSeerrApiKey: element('mrClearSeerr').checked
            });
            populateSettings(result);
            status('mrSettingsStatus', 'Connections saved.', 'success');
            refreshAfterSettings();
        } catch (error) {
            status('mrSettingsStatus', error.message, 'error');
        } finally {
            setSettingsBusy(false);
        }
    }

    async function testConnections() {
        if (settingsBusy || settingsDirty || !settings) return;
        setSettingsBusy(true);
        status('mrSettingsStatus', 'Testing saved connections…');
        const target = element('mrConnectionResults');
        target.hidden = true;
        target.replaceChildren();
        try {
            const results = await request('Connections/Test', 'POST');
            for (const [key, label] of [['sonarr', 'Sonarr'], ['radarr', 'Radarr'], ['seerr', 'Jellyseerr / Seerr']]) {
                const result = results[key];
                target.append(textNode('li', label + ': ' + result.message, result.ok ? 'mr-success' : result.configured ? 'mr-error' : 'secondaryText'));
            }
            target.hidden = false;
            status('mrSettingsStatus');
        } catch (error) {
            status('mrSettingsStatus', error.message, 'error');
        } finally {
            setSettingsBusy(false);
        }
    }

    function updatePagination(loading) {
        element('mrPrevious').disabled = loading || startIndex === 0;
        element('mrNext').disabled = loading || startIndex + pageSize >= totalCount;
    }

    async function loadItems() {
        const version = ++listVersion;
        requesterQueue = [];
        status('mrLibraryStatus', 'Loading movies and series…');
        element('mrLibraryRegion').setAttribute('aria-busy', 'true');
        updatePagination(true);
        try {
            const result = await request('Items', 'GET', undefined, {
                search: element('mrSearch').value.trim(),
                startIndex,
                limit: pageSize
            });
            if (version !== listVersion || destroyed) return;
            totalCount = result.totalCount;
            if (startIndex >= totalCount && startIndex > 0) {
                startIndex = Math.max(0, Math.floor((totalCount - 1) / pageSize) * pageSize);
                await loadItems();
                return;
            }
            const target = element('mrItemRows');
            target.replaceChildren();
            result.items.forEach((item) => {
                const row = document.createElement('tr');
                const title = cell(row, '');
                title.append(button(item.name, () => openReview(item.id)));
                if (item.productionYear) title.append(textNode('span', item.productionYear, 'mr-year secondaryText'));
                title.append(textNode('div', item.type, 'secondaryText mr-note'));
                const requesters = cell(row, 'Loading…', 'mr-names');
                requesterQueue.push({ id: item.id, target: requesters, version });
                const watched = item.users.filter((user) => user.status === 'watched');
                const inProgress = item.users.filter((user) => user.status === 'inProgress');
                const emptyText = item.type === 'Movie' ? 'No available movie' : 'No available episodes';
                renderNames(cell(row, '', 'mr-names mr-success'), watched, item.episodeCount ? 'No one yet' : emptyText, (user) => user.name);
                renderNames(cell(row, '', 'mr-names'), inProgress, item.episodeCount ? 'No one yet' : emptyText,
                    (user) => user.name + (item.type === 'Movie' ? '' : ' (' + user.watchedCount + '/' + item.episodeCount + ')'));
                cell(row, '').dataset.mrVotes = item.id;
                const viewAction = button('View', () => openReview(item.id), 'button-flat');
                viewAction.setAttribute('aria-label', 'View ' + item.name);
                const deleteAction = button('Delete', () => openReview(item.id, true), 'raised button-delete');
                deleteAction.setAttribute('aria-label', 'Delete ' + item.name);
                const actions = textNode('div', '', 'mr-actions');
                actions.append(viewAction, deleteAction);
                cell(row, '').append(actions);
                target.append(row);
            });
            element('mrPageCount').textContent = totalCount
                ? (startIndex + 1) + '–' + (startIndex + result.items.length) + ' of ' + totalCount + ' items'
                : '0 items';
            drainRequesterQueue();
            updateVoteCounts();
            status('mrLibraryStatus', result.items.length ? '' : 'No movies or series found. Try a different search or scan your Jellyfin library.');
        } catch (error) {
            if (version !== listVersion || destroyed) return;
            element('mrItemRows').replaceChildren();
            element('mrPageCount').textContent = '';
            status('mrLibraryStatus', error.message, 'error');
        } finally {
            if (version === listVersion) {
                element('mrLibraryRegion').setAttribute('aria-busy', 'false');
                updatePagination(false);
            }
        }
    }

    function invalidatePreview() {
        preview = null;
        clearTimeout(expiryTimer);
        element('mrPreviewSection').hidden = true;
        element('mrConfirmTitle').value = '';
        element('mrExecuteButton').disabled = true;
    }

    function updateRemovalOptions() {
        const manager = element('mrRemoveSonarr').checked || element('mrRemoveRadarr').checked;
        element('mrDeleteFiles').disabled = !manager || !removalOptionsReady;
        element('mrExclude').disabled = !manager || !removalOptionsReady;
        if (!manager) {
            element('mrDeleteFiles').checked = false;
            element('mrExclude').checked = false;
        }
        element('mrPreviewButton').disabled = removalBusy || !removalOptionsReady || !(manager || element('mrRemoveSeerr').checked);
    }

    function applyRemovalDefaults() {
        const movie = detail?.type === 'Movie';
        const manager = movie ? 'Radarr' : 'Sonarr';
        const sonarr = Boolean(!movie && settings && settings.sonarrUrl && settings.hasSonarrApiKey);
        const radarr = Boolean(movie && settings && settings.radarrUrl && settings.hasRadarrApiKey);
        const seerr = Boolean(settings && settings.seerrUrl && settings.hasSeerrApiKey);
        removalOptionsReady = Boolean(settings);
        element('mrSonarrOption').hidden = movie;
        element('mrRadarrOption').hidden = !movie;
        element('mrRemoveSonarr').checked = sonarr;
        element('mrRemoveSonarr').disabled = !sonarr;
        element('mrRemoveRadarr').checked = radarr;
        element('mrRemoveRadarr').disabled = !radarr;
        element('mrRemoveSeerr').checked = seerr;
        element('mrRemoveSeerr').disabled = !seerr;
        element('mrDeleteFiles').checked = false;
        element('mrExclude').checked = false;
        element('mrDeleteFilesLabel').textContent = 'Also delete ' + (movie ? 'movie' : 'series') + ' files through ' + manager;
        element('mrExcludeLabel').textContent = 'Exclude from ' + manager + ' import lists';
        element('mrDeleteFilesDescription').textContent = movie
            ? 'Deleting files removes the movie files through Radarr. Keeping files leaves them available to Jellyfin until you remove them separately.'
            : 'Deleting files removes the entire Sonarr series folder, including specials and episodes absent from Jellyfin. Keeping files leaves them available to Jellyfin until you remove them separately.';
        element('mrOptionsFields').disabled = removalBusy || !removalOptionsReady;
        status('mrProviderStatus', !settings
            ? 'Connections could not be loaded. Reload connections to remove this item.'
            : !sonarr && !radarr && !seerr ? 'Configure ' + manager + ' or Jellyseerr / Seerr to remove this item.' : '', 'warning');
        element('mrConfigureProviders').hidden = sonarr || radarr || seerr;
        updateRemovalOptions();
    }

    async function openReview(itemId, quickDelete = false) {
        if (removalBusy) return;
        const version = ++detailVersion;
        detail = null;
        removalOptionsReady = false;
        invalidatePreview();
        status('mrRemovalStatus');
        element('mrReviewTitle').textContent = 'Review media';
        element('mrItemDetail').hidden = true;
        element('mrOptionsFields').disabled = true;
        element('mrRemoveSonarr').checked = false;
        element('mrRemoveRadarr').checked = false;
        element('mrRemoveSeerr').checked = false;
        element('mrDeleteFiles').checked = false;
        element('mrExclude').checked = false;
        updateRemovalOptions();
        status('mrProviderStatus', 'Loading connections…');
        element('mrConfigureProviders').hidden = true;
        status('mrDetailStatus', 'Loading viewing progress…');
        if (!reviewDialog.open) reviewDialog.showModal();
        loadDetailRequests(itemId, version);
        const connections = settingsTask || (settings ? Promise.resolve() : loadSettings());
        try {
            const result = await request('Items/' + encodeURIComponent(itemId));
            if (version !== detailVersion || destroyed || !reviewDialog.open) return;
            detail = result;
            renderDetailVotes();
            element('mrReviewTitle').textContent = result.name;
            element('mrProgressSummary').textContent = result.type === 'Movie'
                ? 'Movie · Watched means the movie is marked played, including manual marks.'
                : 'Series · ' + result.episodeCount + ' locally available, non-special episodes. Manually marked episodes count as played.';
            element('mrExecuteButton').textContent = result.type === 'Movie' ? 'Remove movie' : 'Remove series';
            const target = element('mrUserRows');
            target.replaceChildren();
            result.users.forEach((user) => {
                const row = document.createElement('tr');
                cell(row, user.name);
                cell(row, statusLabel(user.status), user.status === 'watched' ? 'mr-success' : '');
                progressCell(row, user, result.episodeCount);
                cell(row, formatDate(user.lastPlayed));
                target.append(row);
            });
            element('mrItemDetail').hidden = false;
            status('mrDetailStatus');
            await connections;
            if (version !== detailVersion || destroyed || !reviewDialog.open) return;
            applyRemovalDefaults();
            if (quickDelete && !element('mrPreviewButton').disabled) await previewRemoval();
        } catch (error) {
            if (version === detailVersion && !destroyed && reviewDialog.open) status('mrDetailStatus', error.message, 'error');
        }
    }

    function setRemovalBusy(busy) {
        removalBusy = busy;
        element('mrOptionsFields').disabled = busy || !removalOptionsReady;
        element('mrCloseReview').disabled = busy;
        element('mrConfirmTitle').disabled = busy;
        element('mrExecuteButton').disabled = busy || !preview || element('mrConfirmTitle').value !== preview.title;
        updateRemovalOptions();
    }

    function showPreview(value) {
        preview = value;
        const target = element('mrPreviewDetails');
        target.replaceChildren();
        addDefinition(target, value.options.mediaType === 'Movie' ? 'Movie' : 'Series', value.title);
        addDefinition(target, 'Jellyfin ID', value.seriesId);
        if (value.options.removeSonarr) {
            addDefinition(target, 'Sonarr', value.sonarr ? value.sonarr.title + ' · Series ID ' + value.sonarr.id + ' · TVDB ' + value.sonarr.tvdbId : 'No matching series found');
            if (value.sonarr) addDefinition(target, 'Sonarr path', value.sonarr.path || 'Unknown');
            addDefinition(target, 'Files', value.options.deleteFiles ? 'Delete all series files through Sonarr' : 'Keep series files');
            addDefinition(target, 'Import lists', value.options.addImportListExclusion ? 'Add an import list exclusion' : 'Do not add an exclusion');
        }
        if (value.options.removeRadarr) {
            addDefinition(target, 'Radarr', value.radarr ? value.radarr.title + ' · Movie ID ' + value.radarr.id + ' · TMDB ' + value.radarr.tmdbId : 'No matching movie found');
            if (value.radarr) addDefinition(target, 'Radarr path', value.radarr.path || 'Unknown');
            addDefinition(target, 'Files', value.options.deleteFiles ? 'Delete movie files through Radarr' : 'Keep movie files');
            addDefinition(target, 'Import lists', value.options.addImportListExclusion ? 'Add an import list exclusion' : 'Do not add an exclusion');
        }
        if (value.options.removeSeerr) {
            addDefinition(target, 'Jellyseerr / Seerr', value.seerr
                ? 'Media ID ' + value.seerr.id + ' · TMDB ' + value.seerr.tmdbId + ' · ' + value.seerr.requestCount + ' request(s)'
                : 'No matching media record found');
        }
        const warnings = element('mrWarnings');
        warnings.replaceChildren();
        (value.warnings || []).forEach((warning) => warnings.append(textNode('li', warning)));
        element('mrWarningsPanel').hidden = !warnings.children.length;
        element('mrConfirmTitle').value = '';
        element('mrConfirmTitle').label('Type “' + value.title + '” exactly to confirm');
        element('mrPreviewExpiry').textContent = 'Preview expires ' + formatDate(value.expiresAt) + '.';
        element('mrPreviewSection').hidden = false;
        clearTimeout(expiryTimer);
        expiryTimer = setTimeout(() => {
            if (preview !== value || removalBusy) return;
            preview = null;
            element('mrExecuteButton').disabled = true;
            element('mrPreviewExpiry').textContent = 'This preview expired. Preview the removal again before confirming.';
        }, Math.max(0, new Date(value.expiresAt).getTime() - Date.now()));
        element('mrConfirmTitle').focus();
    }

    async function previewRemoval(event) {
        event?.preventDefault();
        if (!detail || removalBusy || !removalOptionsReady || !reviewDialog.open || !(element('mrRemoveSonarr').checked || element('mrRemoveRadarr').checked || element('mrRemoveSeerr').checked)) return;
        const version = detailVersion;
        invalidatePreview();
        setRemovalBusy(true);
        status('mrRemovalStatus', 'Checking provider records and viewing progress…');
        try {
            const value = await request('Removals/Preview', 'POST', {
                seriesId: detail.id,
                mediaType: detail.type,
                removeSonarr: element('mrRemoveSonarr').checked,
                removeRadarr: element('mrRemoveRadarr').checked,
                removeSeerr: element('mrRemoveSeerr').checked,
                deleteFiles: element('mrDeleteFiles').checked,
                addImportListExclusion: element('mrExclude').checked
            });
            if (destroyed || version !== detailVersion || !reviewDialog.open) return;
            showPreview(value);
            status('mrRemovalStatus');
        } catch (error) {
            if (version === detailVersion && !destroyed && reviewDialog.open) status('mrRemovalStatus', error.message, 'error');
        } finally {
            setRemovalBusy(false);
            if (preview) element('mrConfirmTitle').focus();
        }
    }

    async function executeRemoval(event) {
        event.preventDefault();
        if (removalBusy || !preview || element('mrConfirmTitle').value !== preview.title) return;
        if (new Date(preview.expiresAt).getTime() <= Date.now()) {
            invalidatePreview();
            status('mrRemovalStatus', 'The preview expired. Preview the removal again.', 'error');
            return;
        }
        const current = preview;
        setRemovalBusy(true);
        status('mrRemovalStatus', 'Removing the selected provider records…');
        try {
            const operation = await request('Removals/' + encodeURIComponent(current.id) + '/Execute', 'POST', {
                confirmationTitle: element('mrConfirmTitle').value
            });
            invalidatePreview();
            element('mrItemDetail').hidden = true;
            renderOperationStatus('mrRemovalStatus', operation);
            if (operation.status !== 'completed') element('mrHistorySection').open = true;
            await Promise.all([loadHistory(), loadItems()]);
        } catch (error) {
            // A lost response does not mean no provider changed. Require a fresh review or an audit retry.
            invalidatePreview();
            status('mrRemovalStatus', error.message + ' Check removal history before trying again.', 'error');
            element('mrHistorySection').open = true;
            await loadHistory();
        } finally {
            setRemovalBusy(false);
        }
    }

    function operationSteps(operation) {
        const steps = [];
        if (operation.options.removeSonarr) steps.push('Sonarr: ' + (operation.sonarrDone ? 'completed' : 'pending'));
        if (operation.options.removeRadarr) steps.push('Radarr: ' + (operation.radarrDone ? 'completed' : 'pending'));
        if (operation.options.removeSeerr) steps.push('Jellyseerr / Seerr: ' + (operation.seerrDone ? 'completed' : 'pending'));
        return steps.join('. ');
    }

    function renderOperationStatus(id, operation) {
        if (operation.status === 'completed') {
            status(id, 'Removal completed. ' + operationSteps(operation) + '. Jellyfin may need a library scan to reflect deleted files.', 'success');
        } else {
            status(id, 'Removal is incomplete. ' + operationSteps(operation) + '. ' + (operation.error || '') + ' Use removal history to retry the remaining steps.', 'error');
        }
    }

    async function loadHistory() {
        const version = ++historyVersion;
        element('mrRefreshHistory').disabled = true;
        status('mrHistoryStatus', 'Loading history…');
        try {
            const operations = await request('Removals');
            if (version !== historyVersion || destroyed) return;
            const target = element('mrHistoryRows');
            target.replaceChildren();
            operations.forEach((operation) => {
                const row = document.createElement('tr');
                cell(row, operation.title).append(textNode('div', operation.options.mediaType === 'Movie' ? 'Movie' : 'Series', 'secondaryText mr-note'));
                const result = cell(row, operation.status === 'completed' ? 'Completed' : 'Incomplete', operation.status === 'completed' ? 'mr-success' : 'mr-error');
                result.append(textNode('div', operationSteps(operation), 'mr-audit-detail secondaryText'));
                if (operation.error) result.append(textNode('div', operation.error, 'mr-audit-detail'));
                cell(row, formatDate(operation.updatedAt));
                const action = cell(row, '');
                if (operation.status === 'partialFailure') {
                    const retry = button('Retry', () => openRetry(operation));
                    retry.setAttribute('aria-label', 'Retry removal of ' + operation.title);
                    action.append(retry);
                }
                target.append(row);
            });
            status('mrHistoryStatus', operations.length ? '' : 'No removals yet.');
        } catch (error) {
            if (version === historyVersion) status('mrHistoryStatus', error.message, 'error');
        } finally {
            if (version === historyVersion) element('mrRefreshHistory').disabled = false;
        }
    }

    function openRetry(operation) {
        if (retryBusy) return;
        retryOperation = operation;
        element('mrRetrySummary').textContent = operation.title + ': ' + operationSteps(operation) + '.';
        element('mrRetryError').textContent = operation.error || '';
        const details = element('mrRetryDetails');
        details.replaceChildren();
        addDefinition(details, 'Operation', operation.id);
        if (operation.options.removeSonarr && !operation.sonarrDone) {
            if (operation.sonarr) {
                addDefinition(details, 'Sonarr series', operation.sonarr.title + ' · ID ' + operation.sonarr.id + ' · TVDB ' + operation.sonarr.tvdbId);
                addDefinition(details, 'Sonarr path', operation.sonarr.path || 'Unknown');
            }
            addDefinition(details, 'Series files', operation.options.deleteFiles ? 'Delete through Sonarr' : 'Keep files');
            addDefinition(details, 'Import lists', operation.options.addImportListExclusion ? 'Add an exclusion' : 'Do not add an exclusion');
        }
        if (operation.options.removeRadarr && !operation.radarrDone) {
            if (operation.radarr) {
                addDefinition(details, 'Radarr movie', operation.radarr.title + ' · ID ' + operation.radarr.id + ' · TMDB ' + operation.radarr.tmdbId);
                addDefinition(details, 'Radarr path', operation.radarr.path || 'Unknown');
            }
            addDefinition(details, 'Movie files', operation.options.deleteFiles ? 'Delete through Radarr' : 'Keep files');
            addDefinition(details, 'Import lists', operation.options.addImportListExclusion ? 'Add an exclusion' : 'Do not add an exclusion');
        }
        if (operation.options.removeSeerr && !operation.seerrDone && operation.seerr) {
            addDefinition(details, 'Jellyseerr / Seerr', 'Media ID ' + operation.seerr.id + ' · TMDB ' + operation.seerr.tmdbId);
        }
        element('mrRetryConfirm').value = '';
        element('mrRetryConfirm').label('Type “' + operation.title + '” exactly to confirm');
        element('mrRetryButton').disabled = true;
        element('mrRetryForm').hidden = false;
        status('mrRetryStatus');
        retryDialog.showModal();
    }

    async function retryRemoval(event) {
        event.preventDefault();
        if (!retryOperation || retryBusy || element('mrRetryConfirm').value !== retryOperation.title) return;
        retryBusy = true;
        element('mrCloseRetry').disabled = true;
        element('mrRetryButton').disabled = true;
        element('mrRetryConfirm').disabled = true;
        status('mrRetryStatus', 'Retrying the remaining provider steps…');
        try {
            const operation = await request('Removals/' + encodeURIComponent(retryOperation.id) + '/Retry', 'POST', {
                confirmationTitle: element('mrRetryConfirm').value
            });
            retryOperation = operation;
            element('mrRetrySummary').textContent = operation.title + ': ' + operationSteps(operation) + '.';
            element('mrRetryError').textContent = operation.error || '';
            element('mrRetryConfirm').value = '';
            element('mrRetryForm').hidden = operation.status === 'completed';
            renderOperationStatus('mrRetryStatus', operation);
            await Promise.all([loadHistory(), loadItems()]);
        } catch (error) {
            element('mrRetryConfirm').value = '';
            status('mrRetryStatus', error.message + ' Refresh removal history before retrying.', 'error');
            await loadHistory();
        } finally {
            retryBusy = false;
            element('mrCloseRetry').disabled = false;
            element('mrRetryConfirm').disabled = false;
        }
    }

    element('mrSettings').addEventListener('submit', saveSettings);
    element('mrSettings').addEventListener('input', () => {
        settingsDirty = true;
        element('mrTestConnections').disabled = true;
        element('mrConnectionResults').hidden = true;
        status('mrSettingsStatus', 'Unsaved changes. Save connections before testing.');
    });
    for (const provider of ['Sonarr', 'Radarr', 'Seerr']) {
        element('mrClear' + provider).addEventListener('change', (event) => {
            const input = element('mr' + provider + 'Key');
            input.disabled = event.target.checked;
            if (event.target.checked) input.value = '';
        });
    }
    element('mrTestConnections').addEventListener('click', testConnections);
    element('mrReloadSettings').addEventListener('click', loadSettings);
    element('mrFilters').addEventListener('submit', (event) => {
        event.preventDefault();
        startIndex = 0;
        loadItems();
    });
    element('mrPrevious').addEventListener('click', () => { startIndex = Math.max(0, startIndex - pageSize); loadItems(); });
    element('mrNext').addEventListener('click', () => { startIndex += pageSize; loadItems(); });
    element('mrRefreshHistory').addEventListener('click', loadHistory);
    element('mrRefreshVotes').addEventListener('click', loadVotes);
    element('mrRemovalOptions').addEventListener('submit', previewRemoval);
    element('mrRemovalOptions').addEventListener('change', () => { invalidatePreview(); updateRemovalOptions(); status('mrRemovalStatus'); });
    element('mrConfirmTitle').addEventListener('input', () => {
        element('mrExecuteButton').disabled = removalBusy || !preview || element('mrConfirmTitle').value !== preview.title;
    });
    element('mrExecuteForm').addEventListener('submit', executeRemoval);
    element('mrCloseReview').addEventListener('click', () => reviewDialog.close());
    element('mrConfigureProviders').addEventListener('click', () => {
        const managerField = detail?.type === 'Movie' ? 'mrRadarrUrl' : 'mrSonarrUrl';
        reviewDialog.close();
        element('mrConnectionsSection').open = true;
        element(settings ? managerField : 'mrReloadSettings').focus();
    });
    reviewDialog.addEventListener('cancel', (event) => { if (removalBusy) event.preventDefault(); });
    reviewDialog.addEventListener('close', () => { ++detailVersion; detail = null; invalidatePreview(); });
    element('mrRetryConfirm').addEventListener('input', () => {
        element('mrRetryButton').disabled = retryBusy || !retryOperation || element('mrRetryConfirm').value !== retryOperation.title;
    });
    element('mrRetryForm').addEventListener('submit', retryRemoval);
    element('mrCloseRetry').addEventListener('click', () => retryDialog.close());
    retryDialog.addEventListener('cancel', (event) => { if (retryBusy) event.preventDefault(); });
    view.addEventListener('viewshow', () => {
        destroyed = false;
        Promise.all([loadSettings(), loadItems(), loadHistory(), loadVotes()]);
    });
    view.addEventListener('viewhide', () => {
        if (reviewDialog.open) reviewDialog.close();
        if (retryDialog.open) retryDialog.close();
    });
    view.addEventListener('viewdestroy', () => {
        destroyed = true;
        ++listVersion;
        ++detailVersion;
        ++historyVersion;
        ++votesVersion;
        requesterQueue = [];
        clearTimeout(expiryTimer);
    });
}
