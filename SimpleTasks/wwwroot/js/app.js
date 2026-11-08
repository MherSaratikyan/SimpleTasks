(function () {
    const token = document.querySelector('meta[name="request-token"]')?.content;
    const panel = document.getElementById('panel');
    const backdrop = document.getElementById('panel-backdrop');
    let panelChanged = false;

    function toast(message) {
        const element = document.createElement('div');
        element.className = 'toast toast-error';
        element.setAttribute('role', 'alert');
        element.textContent = message;
        document.querySelector('.content')?.prepend(element);
    }

    // Slide-in ticket panel
    function openPanel() {
        panel.hidden = false;
        backdrop.hidden = false;
    }

    function closePanel() {
        panel.hidden = true;
        backdrop.hidden = true;
        panel.replaceChildren();
        if (panelChanged) {
            location.reload();
        }
    }

    document.body.addEventListener('htmx:afterSwap', event => {
        if (event.detail.target === panel) {
            openPanel();
        }
    });
    document.body.addEventListener('htmx:beforeRequest', event => {
        if (event.detail.target === panel && event.detail.requestConfig.verb === 'post') {
            panelChanged = true;
        }
    });
    document.body.addEventListener('htmx:responseError', () => toast('That couldn\'t be completed. Please try again.'));
    document.body.addEventListener('htmx:sendError', () => toast('Simple Tasks can\'t be reached right now.'));

    // Sign-in pages have no panel.
    if (panel && backdrop) {
        backdrop.addEventListener('click', closePanel);
        panel.addEventListener('click', event => {
            if (event.target.closest('.panel-close')) {
                closePanel();
            }
        });
        document.addEventListener('keydown', event => {
            if (event.key === 'Escape' && !panel.hidden) {
                closePanel();
            }
        });
    }

    // Light and dark theme: follows the device until the person picks one, then remembers it.
    document.querySelectorAll('[data-theme-toggle]').forEach(button => button.addEventListener('click', () => {
        const root = document.documentElement;
        const current = root.dataset.theme
            ?? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
        const next = current === 'dark' ? 'light' : 'dark';
        root.dataset.theme = next;
        try {
            localStorage.setItem('theme', next);
        } catch {
            // Private windows can refuse storage; the choice then lasts for this page only.
        }
    }));

    // Drag and drop on the board
    const board = document.querySelector('.board');
    if (board?.dataset.canEdit === 'true') {
        board.querySelectorAll('.column-cards').forEach(column => new Sortable(column, {
            group: 'board',
            animation: 120,
            ghostClass: 'card-ghost',
            // Done tickets are locked; clicks on them still open the ticket.
            filter: '.card-locked',
            preventOnFilter: false,
            onEnd: async event => {
                const body = new URLSearchParams({
                    ticketKey: event.item.dataset.key,
                    status: event.to.dataset.status
                });
                if (event.item.previousElementSibling) {
                    body.set('afterKey', event.item.previousElementSibling.dataset.key);
                }
                if (event.item.nextElementSibling) {
                    body.set('beforeKey', event.item.nextElementSibling.dataset.key);
                }

                try {
                    const response = await fetch(board.dataset.moveUrl, {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': token },
                        body
                    });
                    // An expired session redirects to the login page, which loads fine but saved nothing.
                    if (response.redirected && new URL(response.url).pathname.startsWith('/Account/Login')) {
                        location.href = `/Account/Login?ReturnUrl=${encodeURIComponent(location.pathname)}`;
                        return;
                    }
                    if (!response.ok) {
                        const problem = await response.json().catch(() => null);
                        toast(problem?.error ?? 'That move couldn\'t be saved.');
                        setTimeout(() => location.reload(), 1500);
                        return;
                    }
                    // A card dropped into Done is locked straight away, like the server treats it.
                    if (event.to.dataset.status === '3') {
                        event.item.classList.add('card-locked');
                    }
                    board.querySelectorAll('.column').forEach(section => {
                        section.querySelector('h2 span').textContent = section.querySelectorAll('.card').length;
                    });
                } catch {
                    toast('Simple Tasks can\'t be reached right now.');
                }
            }
        }));
    }

    // Live bell
    if (document.getElementById('bell')) {
        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/notifications')
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Error)
            .build();
        // A block body: returning a value here would tell SignalR the client is answering the server.
        connection.on('notify', () => {
            document.body.dispatchEvent(new Event('notify'));
        });
        const start = () => connection.start().catch(() => setTimeout(start, 15000));
        start();
    }
})();
