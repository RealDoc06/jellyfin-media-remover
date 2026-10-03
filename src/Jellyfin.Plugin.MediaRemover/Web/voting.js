/* Loaded alongside Jellyfin Web. Votes are advisory and use only the signed-in user's API. */
(function () {
    'use strict';

    if (window.__mediaRemoverVotingLoaded) return;
    window.__mediaRemoverVotingLoaded = true;

    const clientScript = document.currentScript;
    if (clientScript?.src && !document.getElementById('mrVotingStyles')) {
        const scriptUrl = new URL(clientScript.src);
        const styleUrl = new URL('client.css', scriptUrl);
        styleUrl.search = scriptUrl.search;
        const stylesheet = document.createElement('link');
        stylesheet.id = 'mrVotingStyles';
        stylesheet.rel = 'stylesheet';
        stylesheet.href = styleUrl.href;
        document.head.append(stylesheet);
    }

    const prefix = 'MediaRemover/Voting/';
    const pageSize = 25;
    let session = null;
    let reconcileTimer = null;
    let pendingMenu = null;

    function node(tag, text, className) {
        const result = document.createElement(tag);
        if (text !== undefined) result.textContent = text;
        if (className) result.className = className;
        return result;
    }

    function button(label, action, style = 'raised') {
        const result = node('button', label, 'emby-button ' + style);
        result.type = 'button';
        result.addEventListener('click', action);
        return result;
    }

    function normalize(value) {
        if (Array.isArray(value)) return value.map(normalize);
        if (value && typeof value === 'object') {
            return Object.fromEntries(Object.entries(value).map(([key, child]) => [key[0].toLowerCase() + key.slice(1), normalize(child)]));
        }
        return value;
    }

    function identity() {
        try {
            const api = window.ApiClient;
            const userId = api?.getCurrentUserId();
            if (!userId || (typeof api.accessToken === 'function' && !api.accessToken())) return null;
            return { api, userId, key: api.getUrl(prefix + 'Preferences') + '|' + userId };
        } catch (_) {
            return null;
        }
    }

    function isCurrent(context) {
        return session === context && identity()?.key === context.key;
    }

    async function request(context, path, method = 'GET', body, query) {
        if (!isCurrent(context)) throw new Error('Your Jellyfin session changed.');
        const options = { type: method, url: context.api.getUrl(prefix + path, query), dataType: 'json' };
        if (body !== undefined) {
            options.contentType = 'application/json';
            options.data = JSON.stringify(body);
        }
        try {
            return normalize(await context.api.ajax(options));
        } catch (response) {
            if (response?.status === 401) throw new Error('Your session expired. Sign in again.');
            if (response?.status === 403) throw new Error('Voting is not available for this account.');
            if (response?.status === 404) throw new Error('This item is no longer available. Refresh the list.');
            throw new Error('Unable to reach voting. Try again.');
        }
    }

    function setMessage(target, message, error = false) {
        target.textContent = message;
        target.className = 'mr-voting-message' + (error ? ' infoBanner' : '');
        target.setAttribute('role', error ? 'alert' : 'status');
        target.hidden = !message;
    }

    function mediaType(item) {
        const labels = {
            Series: 'Series', Movie: 'Movie', Video: 'Video', MusicVideo: 'Music video',
            MusicAlbum: 'Album', MusicArtist: 'Artist', Audio: 'Track', AudioBook: 'Audiobook',
            Book: 'Book', Photo: 'Photo', PhotoAlbum: 'Photo album', BoxSet: 'Collection',
            Playlist: 'Playlist', Folder: 'Folder'
        };
        return labels[item.type] || 'Media';
    }

    function progressText(item, compact) {
        const count = item.itemCount;
        const unit = item.progressUnit || 'item';
        const units = unit + (count === 1 ? '' : 's');
        if (!count || item.status === 'empty') return 'No ' + unit + 's available';
        const video = ['Series', 'Movie', 'Video', 'MusicVideo'].includes(item.type);
        const audio = ['Audio', 'AudioBook', 'MusicAlbum', 'MusicArtist'].includes(item.type);
        const photo = ['Photo', 'PhotoAlbum'].includes(item.type);
        const completed = video ? 'Watched' : audio ? 'Played' : photo ? 'Viewed' : item.type === 'Book' ? 'Read' : 'Completed';
        const unwatched = completed === 'Completed' ? 'Not started' : 'Not ' + completed.toLowerCase();
        const aggregate = count > 1 || ['Series', 'MusicAlbum', 'MusicArtist', 'BoxSet', 'Playlist', 'PhotoAlbum', 'Folder'].includes(item.type);
        if (!aggregate || compact) {
            if (item.status === 'watched') return completed;
            if (item.status === 'unwatched') return unwatched;
            return aggregate ? item.playedCount + '/' + count + ' ' + completed.toLowerCase() : 'In progress';
        }
        if (item.status === 'watched') return 'All ' + count + ' ' + units + ' ' + completed.toLowerCase();
        if (item.status === 'unwatched') return unwatched + ' · ' + count + ' ' + units;
        return item.playedCount + '/' + count + ' ' + units + ' ' + completed.toLowerCase()
            + (item.inProgressCount ? ' · ' + item.inProgressCount + ' in progress' : '');
    }

    function updateRow(context, row, item) {
        const toggle = row.querySelector('[data-mr-vote]');
        toggle.textContent = item.myVote ? (row.classList.contains('mr-voting-compact-row') ? 'Undo vote' : 'Withdraw vote') : 'OK to delete';
        toggle.setAttribute('aria-pressed', String(item.myVote));
        toggle.setAttribute('aria-label', (item.myVote ? 'Withdraw vote for ' : 'OK to delete ') + item.name);
        toggle.disabled = context.busy.has(item.id);
        row.querySelector('[data-mr-vote-count]').textContent = item.voteCount + (item.voteCount === 1 ? ' vote' : ' votes');
        const detail = row.querySelector('.mr-voting-detail');
        detail.title = detail.textContent;
    }

    function mediaRow(context, item, message, compact = false) {
        const row = node('div', undefined, 'listItem listItem-border mr-voting-row' + (compact ? ' mr-voting-compact-row' : ''));
        row.dataset.mrItem = item.id;
        const info = node('div', undefined, 'listItemBody');
        const name = node('div', item.name + (item.productionYear ? ' (' + item.productionYear + ')' : '')
            + (compact && item.context ? ' · ' + item.context : ''), 'listItemBodyText mr-voting-name');
        name.title = name.textContent;
        const watched = node('span', progressText(item, compact));
        const count = node('span', '');
        count.dataset.mrVoteCount = '';
        const detail = node('div', undefined, 'listItemBodyText secondary mr-voting-detail');
        const type = node('span', mediaType(item) + (!compact && item.context ? ' · ' + item.context : ''));
        detail.append(type, document.createTextNode(' · '), count, document.createTextNode(' · '), watched);
        info.append(name, detail);
        const toggle = button('', () => castVote(context, item, message));
        toggle.dataset.mrVote = '';
        row.append(info, toggle);
        updateRow(context, row, item);
        return row;
    }

    function updateVisibleRows(context) {
        for (const view of [context.home, context.dialog]) {
            if (!view) continue;
            for (const row of view.list.querySelectorAll('[data-mr-item]')) {
                const item = view.items.find(candidate => candidate.id === row.dataset.mrItem);
                if (item) updateRow(context, row, item);
            }
        }
    }

    async function castVote(context, item, message) {
        if (!isCurrent(context) || context.busy.has(item.id)) return;
        context.busy.add(item.id);
        updateVisibleRows(context);
        setMessage(message, 'Saving vote…');
        try {
            const result = await request(context, 'Items/' + encodeURIComponent(item.id) + '/Vote', 'PUT', { approved: !item.myVote });
            if (!isCurrent(context)) return;
            context.voteResults.set(item.id, { result, version: ++context.voteVersion });
            for (const view of [context.home, context.dialog]) {
                for (const entry of view?.items || []) {
                    if (entry.id === item.id) Object.assign(entry, { myVote: result.myVote, voteCount: result.voteCount, votedAt: result.votedAt });
                }
            }
            Object.assign(item, { myVote: result.myVote, voteCount: result.voteCount, votedAt: result.votedAt });
            setMessage(message, result.myVote ? 'Vote saved. Only the admin can delete.' : 'Vote withdrawn.');
            if (context.home) loadHome(context, context.home, true);
        } catch (error) {
            if (isCurrent(context)) setMessage(message, error.message, true);
        } finally {
            context.busy.delete(item.id);
            if (isCurrent(context)) updateVisibleRows(context);
        }
    }

    async function loadPreferences(context) {
        if (context.preferenceTask) return context.preferenceTask;
        context.preferenceError = '';
        context.preferenceTask = (async () => {
            try {
                const result = await request(context, 'Preferences');
                if (isCurrent(context)) context.preferences = result;
            } catch (error) {
                if (isCurrent(context)) context.preferenceError = error.message;
            } finally {
                context.preferenceTask = null;
                if (isCurrent(context)) {
                    updatePreferenceButton(context);
                    scheduleReconcile();
                }
            }
        })();
        return context.preferenceTask;
    }

    async function setHomeDismissed(context, dismissed, message) {
        if (!isCurrent(context) || context.preferenceBusy) return;
        context.preferenceBusy = true;
        updatePreferenceButton(context);
        if (context.home) context.home.dismiss.disabled = true;
        try {
            const result = await request(context, 'Preferences', 'PUT', { homeDismissed: dismissed });
            if (!isCurrent(context)) return;
            context.preferences = result;
            context.preferenceError = '';
            setMessage(message, dismissed ? 'Hidden from Home. Deletion votes remains in the side menu.' : 'Deletion votes will appear on Home.');
            reconcile();
        } catch (error) {
            if (isCurrent(context)) setMessage(message, error.message, true);
        } finally {
            context.preferenceBusy = false;
            if (isCurrent(context)) {
                updatePreferenceButton(context);
                if (context.home) context.home.dismiss.disabled = false;
            }
        }
    }

    function updatePreferenceButton(context) {
        const toggle = context.dialog?.preference;
        if (!toggle) return;
        toggle.disabled = context.preferenceBusy || !!context.preferenceTask;
        toggle.textContent = context.preferences
            ? (context.preferences.homeDismissed ? 'Show on Home' : 'Hide from Home')
            : (context.preferenceError ? 'Retry Home preference' : 'Loading Home preference…');
    }

    function renderList(context, view, items, voteVersion) {
        // A list request started before a completed vote must not restore the old toggle/count.
        for (const item of items) {
            const saved = context.voteResults.get(item.id);
            if (saved && saved.version > voteVersion) {
                Object.assign(item, { myVote: saved.result.myVote, voteCount: saved.result.voteCount, votedAt: saved.result.votedAt });
            }
        }
        view.items = items;
        view.list.replaceChildren(...items.map(item => mediaRow(context, item, view.message, view === context.home)));
        if (!items.length) view.list.append(node('p', view === context.home ? 'No votes from other users yet.' : 'No media found.', 'mr-voting-empty'));
    }

    function mountHome(context, host) {
        if (context.home?.root.isConnected) return;
        const root = node('section', undefined, 'verticalSection padded-left padded-right mr-voting-home');
        root.id = 'mrVotingHome';
        root.dataset.mrVoting = '';
        root.setAttribute('aria-labelledby', 'mrVotingHomeTitle');
        const heading = node('div', undefined, 'mr-voting-heading');
        const title = node('h2', 'Deletion votes', 'sectionTitle');
        title.id = 'mrVotingHomeTitle';
        const browse = button('Browse all', () => openDialog(context), 'button-flat');
        const message = node('p');
        const dismiss = button('', () => setHomeDismissed(context, true, message), 'paper-icon-button-light');
        const closeIcon = node('span', '', 'material-icons close');
        closeIcon.setAttribute('aria-hidden', 'true');
        dismiss.append(closeIcon);
        dismiss.title = 'Dismiss';
        dismiss.setAttribute('aria-label', 'Dismiss deletion votes from Home');
        heading.append(title, browse, dismiss);
        const list = node('div', undefined, 'mr-voting-list');
        root.append(heading, message, list);
        const view = { root, list, message, dismiss, items: [], version: 0 };
        context.home = view;
        host.prepend(root);
        if (context.preferenceError) {
            setMessage(message, context.preferenceError, true);
            list.append(button('Retry', async () => {
                await loadPreferences(context);
                if (isCurrent(context) && context.preferences && context.home === view) loadHome(context, view);
            }, 'button-flat'));
        } else loadHome(context, view);
    }

    async function loadHome(context, view, quiet = false) {
        const version = ++view.version;
        const voteVersion = context.voteVersion;
        if (!quiet) setMessage(view.message, 'Loading votes…');
        try {
            const result = await request(context, 'Items/Home', 'GET', undefined, { limit: 3 });
            if (!isCurrent(context) || context.home !== view || version !== view.version) return;
            const focusedItem = view.list.contains(document.activeElement) ? document.activeElement.closest('[data-mr-item]')?.dataset.mrItem : null;
            if (!quiet) setMessage(view.message, '');
            renderList(context, view, result.items, voteVersion);
            if (focusedItem) {
                const row = Array.from(view.list.children).find(child => child.dataset.mrItem === focusedItem);
                (row?.querySelector('[data-mr-vote]') || view.root.querySelector('button')).focus();
            }
        } catch (error) {
            if (!isCurrent(context) || context.home !== view || version !== view.version) return;
            setMessage(view.message, error.message, true);
            view.list.replaceChildren(button('Retry', () => loadHome(context, view), 'button-flat'));
        }
    }

    function closeDialog(context) {
        const view = context.dialog;
        if (!view) return;
        context.dialog = null;
        clearTimeout(view.searchTimer);
        if (view.root.open) view.root.close();
        view.root.remove();
        if (isCurrent(context) && view.returnFocus?.isConnected) view.returnFocus.focus();
    }

    function openDialog(context) {
        if (!isCurrent(context) || context.dialog) return;
        const root = node('dialog', undefined, 'dialog mr-voting-dialog');
        root.id = 'mrVotingDialog';
        root.dataset.mrVoting = '';
        root.setAttribute('aria-labelledby', 'mrVotingDialogTitle');
        const header = node('div', undefined, 'mr-voting-heading');
        const title = node('h2', 'Deletion votes');
        title.id = 'mrVotingDialogTitle';
        header.append(title, button('Close', () => closeDialog(context), 'button-flat'));
        const controls = node('div', undefined, 'mr-voting-controls');
        const searchLabel = node('label', 'Search media', 'inputLabel');
        searchLabel.htmlFor = 'mrVotingSearch';
        const search = node('input', undefined, 'emby-input');
        search.id = 'mrVotingSearch';
        search.type = 'search';
        search.autocomplete = 'off';
        const searchBox = node('div', undefined, 'inputContainer mr-voting-search');
        searchBox.append(searchLabel, search);
        const message = node('p');
        const preference = button('', async () => {
            if (!context.preferences) {
                await loadPreferences(context);
                if (isCurrent(context) && context.preferenceError) setMessage(message, context.preferenceError, true);
            } else setHomeDismissed(context, !context.preferences.homeDismissed, message);
        }, 'button-flat');
        controls.append(searchBox, preference);
        const list = node('div', undefined, 'paperList mr-voting-list');
        const paging = node('div', undefined, 'mr-voting-paging');
        const count = node('span');
        count.setAttribute('role', 'status');
        const previous = button('Previous', () => { view.startIndex = Math.max(0, view.startIndex - pageSize); loadDialog(context, view); }, 'button-flat');
        const next = button('Next', () => { view.startIndex += pageSize; loadDialog(context, view); }, 'button-flat');
        paging.append(count, previous, next);
        root.append(header, node('p', 'Mark media you’re OK with deleting. Episodes and seasons vote for the whole series. Only the admin can delete.', 'mr-voting-description'), controls, message, list, paging);
        const view = { root, list, message, preference, search, count, previous, next, items: [], startIndex: 0, totalCount: 0, version: 0, searchTimer: null, returnFocus: document.activeElement };
        context.dialog = view;
        root.addEventListener('cancel', event => { event.preventDefault(); closeDialog(context); });
        root.addEventListener('close', () => { if (context.dialog === view) closeDialog(context); });
        search.addEventListener('input', () => {
            clearTimeout(view.searchTimer);
            ++view.version;
            previous.disabled = true;
            next.disabled = true;
            view.searchTimer = setTimeout(() => { view.startIndex = 0; loadDialog(context, view); }, 250);
        });
        document.body.append(root);
        updatePreferenceButton(context);
        root.showModal();
        search.focus();
        loadDialog(context, view);
    }

    async function loadDialog(context, view) {
        const version = ++view.version;
        const voteVersion = context.voteVersion;
        view.previous.disabled = true;
        view.next.disabled = true;
        setMessage(view.message, 'Loading media…');
        try {
            const result = await request(context, 'Items', 'GET', undefined, { search: view.search.value.trim(), startIndex: view.startIndex, limit: pageSize });
            if (!isCurrent(context) || context.dialog !== view || view.version !== version) return;
            // Library changes can remove the last page while this view is open.
            if (!result.items.length && result.totalCount && view.startIndex >= result.totalCount) {
                view.startIndex = Math.floor((result.totalCount - 1) / pageSize) * pageSize;
                return loadDialog(context, view);
            }
            view.totalCount = result.totalCount;
            setMessage(view.message, '');
            renderList(context, view, result.items, voteVersion);
            view.count.textContent = result.totalCount ? (view.startIndex + 1) + '–' + (view.startIndex + result.items.length) + ' of ' + result.totalCount : '0 items';
            view.previous.disabled = view.startIndex === 0;
            view.next.disabled = view.startIndex + result.items.length >= result.totalCount;
        } catch (error) {
            if (!isCurrent(context) || context.dialog !== view || view.version !== version) return;
            setMessage(view.message, error.message, true);
            view.count.textContent = '';
            view.list.replaceChildren(button('Retry', () => loadDialog(context, view), 'button-flat'));
        }
    }

    function mountNavigation(context, host) {
        if (context.nav?.isConnected) return;
        const link = node('a', undefined, 'navMenuOption emby-button mr-voting-nav');
        link.id = 'mrVotingNav';
        link.dataset.mrVoting = '';
        link.href = '#';
        link.setAttribute('role', 'button');
        link.setAttribute('aria-haspopup', 'dialog');
        const icon = node('span', '', 'material-icons navMenuOptionIcon how_to_vote');
        icon.setAttribute('aria-hidden', 'true');
        link.append(icon, node('span', 'Deletion votes', 'navMenuOptionText'));
        link.addEventListener('click', event => { event.preventDefault(); openDialog(context); });
        link.addEventListener('keydown', event => {
            if (event.key === ' ') { event.preventDefault(); link.click(); }
        });
        context.nav = link;
        host.append(link);
    }

    // Bind only to a menu-opening gesture, never to a previously selected library item.
    function captureMenu(event) {
        if (event.type === 'command' && event.detail?.command !== 'menu') return;
        if (!(event.target instanceof Element) || event.target.closest('.actionSheet, [data-mr-voting]')) return;
        const target = event.target;
        const menuAction = event.type === 'command' ? event.detail?.command === 'menu'
            : event.type === 'contextmenu' || !!target.closest('[data-action="menu"], .btnMoreCommands');
        if (pendingMenu && Date.now() > pendingMenu.expires) pendingMenu = null;
        if (!menuAction || !session || !isCurrent(session)) {
            if (pendingMenu) pendingMenu.ambiguous = true;
            return;
        }
        const card = target.closest('[data-id][data-type]');
        let id = card?.dataset.id;
        const serverId = card?.dataset.serverid;
        if ((serverId && serverId !== session.api.serverId()) || (card && ['CollectionFolder', 'UserView', 'Genre', 'MusicGenre', 'Studio', 'Person', 'Year'].includes(card.dataset.type))) {
            if (pendingMenu) pendingMenu.ambiguous = true;
            return;
        }
        if (!id && target.closest('.btnMoreCommands') && window.location.hash.startsWith('#/details?')) {
            const params = new URLSearchParams(window.location.hash.split('?')[1]);
            if (params.get('serverId') && params.get('serverId') !== session.api.serverId()) {
                if (pendingMenu) pendingMenu.ambiguous = true;
                return;
            }
            id = params.get('id');
        }
        // Native menus open asynchronously. Overlapping gestures have no reliable sheet-to-item
        // association, so omit our action for that burst rather than bind it to another item.
        if (pendingMenu && (pendingMenu.ambiguous || pendingMenu.id !== id)) pendingMenu.ambiguous = true;
        else if (id) pendingMenu = { context: session, id, expires: Date.now() + 5000 };
    }

    async function mountMenu(sheet) {
        const target = pendingMenu;
        if (!target || target.ambiguous || Date.now() > target.expires || !isCurrent(target.context)) return;
        // Photos lack playback/playlist commands, but still have native item actions.
        if (!sheet.querySelector('[data-id="resume"], [data-id="play"], [data-id="shuffle"], [data-id="delete"], [data-id="addtoplaylist"], [data-id="download"], [data-id="editimages"], [data-id="multiSelect"]')) return;
        pendingMenu = null;
        const context = target.context;
        const menu = sheet.querySelector('.actionSheetScroller');
        if (!menu) return;
        try {
            // The server checks access and resolves episodes/seasons to their parent series.
            const item = await request(context, 'Items/' + encodeURIComponent(target.id));
            if (!isCurrent(context) || !sheet.isConnected) return;
            const action = button('', () => {
                if (!isCurrent(context) || context.busy.has(item.id)) return;
                context.notice?.remove();
                clearTimeout(context.noticeTimer);
                const notice = node('div', undefined, 'toast mr-voting-toast');
                notice.dataset.mrVoting = '';
                const message = node('span');
                notice.append(message);
                context.notice = notice;
                document.body.append(notice);
                castVote(context, item, message).finally(() => {
                    context.noticeTimer = setTimeout(() => notice.remove(), 5000);
                });
            }, 'listItem listItem-button actionSheetMenuItem');
            action.dataset.mrVoting = '';
            action.dataset.mrMenuVote = '';
            // No native command ID: Jellyfin dismisses its menu as a normal cancellation.
            const icon = node('span', '', 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons how_to_vote');
            icon.setAttribute('aria-hidden', 'true');
            const body = node('div', undefined, 'listItemBody actionsheetListItemBody');
            body.append(node('div', item.myVote ? 'Withdraw vote' : 'Vote to delete ' + mediaType(item).toLowerCase(), 'listItemBodyText actionSheetItemText'),
                node('div', item.name + (item.context ? ' · ' + item.context : ''), 'listItemBodyText secondary'));
            action.append(icon, body);
            // Prepend so the action stays reachable in long menus on small screens.
            sheet.classList.add('mr-voting-menu');
            menu.prepend(action);
            // Jellyfin positioned the sheet before this async action changed its dimensions.
            if (sheet.style.left) sheet.style.left = Math.max(8, Math.min(parseFloat(sheet.style.left), document.documentElement.clientWidth - sheet.offsetWidth - 8)) + 'px';
            if (sheet.style.top) sheet.style.top = Math.max(8, Math.min(parseFloat(sheet.style.top), window.innerHeight - sheet.offsetHeight - 8)) + 'px';
        } catch (_) {
            // Hidden/missing items or unavailable voting must not interfere with Jellyfin's menu.
        }
    }

    function resetSession() {
        if (!session) return;
        const previous = session;
        session = null;
        pendingMenu = null;
        previous.notice?.remove();
        clearTimeout(previous.noticeTimer);
        document.querySelectorAll('[data-mr-menu-vote]').forEach(action => action.remove());
        closeDialog(previous);
        previous.home?.root.remove();
        previous.nav?.remove();
    }

    function reconcile() {
        const current = identity();
        if (current?.key !== session?.key) {
            resetSession();
            if (current) {
                session = { ...current, preferences: null, preferenceTask: null, preferenceError: '', preferenceBusy: false, busy: new Set(), voteResults: new Map(), voteVersion: 0, home: null, nav: null, dialog: null };
                loadPreferences(session);
            }
        }
        if (!session) return;
        const route = window.location.hash.slice(1).split('?')[0];
        const isDashboard = /^\/(dashboard|configurationpage)(\/|$)/i.test(route);
        const isSignedOutPage = /^\/(login|selectserver|addserver|connectlogin|wizard)(\/|$)/i.test(route);
        if (isDashboard || isSignedOutPage) {
            closeDialog(session);
            session.nav?.remove();
            session.home?.root.remove();
            session.home = null;
            return;
        }
        const navHost = document.querySelector('.mainDrawer-scrollContainer > .customMenuOptions');
        if (navHost) mountNavigation(session, navHost);
        const homeHost = document.querySelector('#indexPage #homeTab');
        const isHome = route === '/home' || route === '/' || route === '';
        if (isHome && homeHost && !session.preferences?.homeDismissed && (session.preferences || session.preferenceError)) {
            mountHome(session, homeHost);
        } else if (session.home) {
            session.home.root.remove();
            session.home = null;
        }
    }

    function scheduleReconcile() {
        if (reconcileTimer !== null) return;
        reconcileTimer = setTimeout(() => { reconcileTimer = null; reconcile(); }, 80);
    }

    // Jellyfin recreates the drawer and caches Home. Observe those mounts, ignoring our own DOM writes.
    const observer = new MutationObserver(records => {
        for (const record of records) {
            if (record.target instanceof Element && record.target.closest('[data-mr-voting]')) continue;
            if (record.target instanceof Element && record.target.matches('#homeTab, .mainDrawer-scrollContainer, .customMenuOptions')) {
                scheduleReconcile();
                return;
            }
            for (const changed of [...record.addedNodes, ...record.removedNodes]) {
                if (!(changed instanceof Element) || changed.matches('[data-mr-voting]')) continue;
                if (changed.isConnected && (changed.matches('.actionSheet') || changed.querySelector('.actionSheet'))) {
                    for (const sheet of changed.matches('.actionSheet') ? [changed] : changed.querySelectorAll('.actionSheet')) mountMenu(sheet);
                }
                if (changed.matches('.page, .mainDrawer, .skinHeader, #homeTab, .customMenuOptions') || changed.querySelector('.page, .mainDrawer, #homeTab, .customMenuOptions')) {
                    scheduleReconcile();
                    return;
                }
            }
        }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    document.addEventListener('click', captureMenu, true);
    document.addEventListener('contextmenu', captureMenu, true);
    document.addEventListener('command', captureMenu, true);
    window.addEventListener('hashchange', () => { if (pendingMenu) pendingMenu.ambiguous = true; if (session) closeDialog(session); reconcile(); });
    window.addEventListener('focus', scheduleReconcile);
    window.addEventListener('pageshow', scheduleReconcile);
    document.addEventListener('pageshow', scheduleReconcile, true);
    document.addEventListener('visibilitychange', () => { if (!document.hidden) scheduleReconcile(); });
    document.addEventListener('DOMContentLoaded', scheduleReconcile);
    scheduleReconcile();
})();
