(function () {
    const header = document.querySelector('#site-header');
    const menuToggle = document.querySelector('.menu-toggle');
    const nav = document.querySelector('#main-nav');
    const languageToggle = document.querySelector('[data-language-toggle]');
    const languageOptions = [...document.querySelectorAll('[data-language-option]')];
    const revealItems = document.querySelectorAll('.reveal');
    const carousel = document.querySelector('[data-carousel]');

    const translations = {
        en: {
            documentTitle: 'CxShell · A clear remote workspace',
            metaDescription: 'CxShell is a cross-platform remote workspace built with Avalonia and AtomUI.',
            brandHome: 'CxShell home',
            mainNav: 'Main navigation',
            navCapabilities: 'Capabilities',
            navTechnology: 'Technology',
            navPrinciples: 'Principles',
            navSupport: 'Support',
            languageToggle: 'Switch to Chinese',
            menu: 'Menu',
            openMenu: 'Open navigation',
            heroEyebrow: "Cross-platform remote workspace <span class='eyebrow-mark'>/</span> Open source project",
            heroLede: 'Put terminals, sessions, files, and Agent into one clear desktop.',
            heroCopy: 'A cross-platform remote connection tool for developers and operators. Less switching, more awareness of the work in front of you.',
            downloadLatest: 'Download latest',
            viewSource: 'View source',
            scrollCue: 'Explore CxShell capabilities',
            explore: 'Explore',
            introIndex: '01 <span>/</span> Capabilities',
            introTitle: 'Everything you need for daily connections, <em>in one workspace.</em>',
            introCopy: 'CxShell brings connections, operations, observation, and assistance together so your desktop serves the task at hand instead of making the task chase windows.',
            capabilityTerminalTitle: 'Terminal first',
            capabilityTerminalCopy: 'SSH, TELNET, RLOGIN, serial, and local Terminal sessions, all in one tabbed workspace.',
            capabilityFilesTitle: 'Files and state',
            capabilityFilesCopy: 'SFTP file management, transfer queues, server monitoring, and remote desktop in one session experience.',
            capabilityAgentTitle: 'Agent assistance',
            capabilityAgentCopy: 'Access open SSH sessions within clear permission boundaries, with diagnostics, long tasks, approvals, and run summaries.',
            workspaceIndex: '02 <span>/</span> Workspace',
            workspaceTitle: 'One window,<br><em>many remote contexts.</em>',
            workspaceScreenshots: 'Workspace screenshots',
            viewSshWorkspace: 'View SSH workspace',
            viewRdpWorkspace: 'View RDP workspace',
            previousSlide: 'Previous slide',
            nextSlide: 'Next slide',
            sshWorkspaceAlt: 'CxShell SSH, SFTP, monitoring, and Agent workspace',
            rdpWorkspaceAlt: 'CxShell RDP session workspace',
            stackIndex: '03 <span>/</span> Technology',
            stackTitle: 'A native desktop experience,<br><em>an open technical foundation.</em>',
            stackCopy: 'CxShell is built with .NET, Avalonia, and AtomUI. It is not a remote tool wrapped in a browser, but an open-source project refined for desktop workflows.',
            stackUiCopy: 'Unified desktop controls, themes, and interaction foundations that keep Avalonia pages restrained, clear, and extensible.',
            stackAppCopy: 'A cross-platform desktop framework for Windows, macOS, and Linux, preserving the responsiveness a desktop app should have.',
            stackCoreCopy: 'Protocols, transfers, monitoring, Agent Runtime, and native bridges organized through MVVM and clear service boundaries.',
            stackOpenCopy: 'Code, documentation, and iteration stay public on GitHub. Feedback, suggestions, and contributions are welcome.',
            storyIndex: '04 <span>/</span> Principles',
            storyTitle: 'Tools should make complex work <em>feel more orderly.</em>',
            storyCopy: 'CxShell grows from real terminal, file transfer, and remote troubleshooting scenarios. AI collaboration has helped explore, build, and iterate on the project, but every boundary, permission model, and interaction choice is guided by real use.',
            suggestIdea: 'Suggest an idea on GitHub',
            supportIndex: '05 <span>/</span> Support',
            supportTitle: "Open source is the start,<br><em>let's make it better together.</em>",
            supportCopy: 'CxShell is free and open source. If it saves you a little time, give the project a Star or support continued development in whatever way works for you.',
            starOnGithub: 'Star on GitHub',
            wechatQrAlt: 'WeChat Pay donation QR code',
            wechatPay: 'WeChat Pay',
            alipayQrAlt: 'Alipay donation QR code',
            alipay: 'Alipay',
            footerDescription: 'Cross-platform remote workspace · Apache License 2.0'
        },
        zh: {
            documentTitle: 'CxShell · 一个清晰的远程工作台',
            metaDescription: 'CxShell 是一个基于 Avalonia 和 AtomUI 构建的跨平台远程工作台。',
            brandHome: 'CxShell 首页',
            mainNav: '主导航',
            navCapabilities: '能力',
            navTechnology: '技术',
            navPrinciples: '理念',
            navSupport: '支持项目',
            languageToggle: '切换为英文',
            menu: '菜单',
            openMenu: '打开导航',
            heroEyebrow: "跨平台远程工作台 <span class='eyebrow-mark'>/</span> 开源项目",
            heroLede: '把终端、会话、文件和 Agent，放进一个清晰的桌面。',
            heroCopy: '面向开发者和运维人员的跨平台远程连接工具。少一点来回切换，多一点对当前工作状态的掌握。',
            downloadLatest: '下载最新版本',
            viewSource: '查看源码',
            scrollCue: '向下查看 CxShell 的能力',
            explore: '继续了解',
            introIndex: '01 <span>/</span> 能力',
            introTitle: '日常连接需要的，<em>都在同一个工作区。</em>',
            introCopy: 'CxShell 把连接、操作、观察和辅助串在一起，让桌面空间服务于当前任务，而不是让任务追着窗口跑。',
            capabilityTerminalTitle: '终端优先',
            capabilityTerminalCopy: 'SSH、TELNET、RLOGIN、串口和本地 Terminal，统一在标签工作区里运行。',
            capabilityFilesTitle: '文件与状态',
            capabilityFilesCopy: 'SFTP 文件管理、传输队列、服务器监控和远程桌面，共享同一套会话体验。',
            capabilityAgentTitle: 'Agent 辅助',
            capabilityAgentCopy: '通过权限边界访问已打开的 SSH 会话，支持诊断、长任务、审批和运行摘要。',
            workspaceIndex: '02 <span>/</span> 工作区',
            workspaceTitle: '一个窗口，<br><em>多种远程现场。</em>',
            workspaceScreenshots: '工作区截图',
            viewSshWorkspace: '查看 SSH 工作区',
            viewRdpWorkspace: '查看 RDP 工作区',
            previousSlide: '上一张',
            nextSlide: '下一张',
            sshWorkspaceAlt: 'CxShell 的 SSH、SFTP、监控和 Agent 工作区',
            rdpWorkspaceAlt: 'CxShell 的 RDP 会话工作区',
            stackIndex: '03 <span>/</span> 技术',
            stackTitle: '原生桌面体验，<br><em>开放的技术底座。</em>',
            stackCopy: 'CxShell 基于 .NET、Avalonia 和 AtomUI 构建。它不是套在浏览器里的远程工具，而是面向桌面工作流持续打磨的开源项目。',
            stackUiCopy: '统一的桌面控件、主题和交互基础，让 Avalonia 页面保持克制、清晰和可扩展。',
            stackAppCopy: '跨 Windows、macOS 和 Linux 的原生桌面框架，保留桌面应用应有的响应感。',
            stackCoreCopy: '以 MVVM 和服务边界组织协议、传输、监控、Agent Runtime 与原生桥接能力。',
            stackOpenCopy: '代码、文档和迭代过程公开在 GitHub，欢迎反馈问题、提交建议和参与改进。',
            storyIndex: '04 <span>/</span> 理念',
            storyTitle: '工具应该让复杂的工作，<em>看起来更有秩序。</em>',
            storyCopy: 'CxShell 从真实的终端、文件传输和远程排障场景出发。项目也大量借助 AI 协作完成探索、实现和迭代，但每一个功能边界、权限设计和交互取舍，都以实际使用为准。',
            suggestIdea: '在 GitHub 提出建议',
            supportIndex: '05 <span>/</span> 支持项目',
            supportTitle: '开源是起点，<br><em>一起把它做得更好。</em>',
            supportCopy: 'CxShell 免费开源。如果它帮你省下了一点时间，欢迎给项目一个 Star，或用你方便的方式支持持续开发。',
            starOnGithub: '在 GitHub 点 Star',
            wechatQrAlt: '微信支付捐助二维码',
            wechatPay: '微信支付',
            alipayQrAlt: '支付宝捐助二维码',
            alipay: '支付宝',
            footerDescription: '跨平台远程工作台 · Apache License 2.0'
        }
    };

    const applyLanguage = (language) => {
        const activeLanguage = translations[language] ? language : 'en';
        const dictionary = translations[activeLanguage];
        document.documentElement.lang = activeLanguage === 'zh' ? 'zh-CN' : 'en';
        document.title = dictionary.documentTitle;

        document.querySelector('meta[name="description"]')?.setAttribute('content', dictionary.metaDescription);
        document.querySelectorAll('[data-i18n]').forEach((element) => {
            const value = dictionary[element.dataset.i18n];
            if (value !== undefined) element.textContent = value;
        });
        document.querySelectorAll('[data-i18n-html]').forEach((element) => {
            const value = dictionary[element.dataset.i18nHtml];
            if (value !== undefined) element.innerHTML = value;
        });
        document.querySelectorAll('[data-i18n-aria]').forEach((element) => {
            const value = dictionary[element.dataset.i18nAria];
            if (value !== undefined) element.setAttribute('aria-label', value);
        });
        document.querySelectorAll('[data-i18n-alt]').forEach((element) => {
            const value = dictionary[element.dataset.i18nAlt];
            if (value !== undefined) element.setAttribute('alt', value);
        });
        languageToggle?.setAttribute('aria-label', dictionary.languageToggle);
        languageToggle?.setAttribute('aria-pressed', String(activeLanguage === 'zh'));
        languageOptions.forEach((option) => {
            option.classList.toggle('is-active', option.dataset.languageOption === activeLanguage);
        });
        const activeSlide = carousel?.querySelector('.carousel-slide.is-active');
        const captionTitle = carousel?.querySelector('[data-carousel-title]');
        if (captionTitle && activeSlide) {
            const captionKey = activeLanguage === 'zh' ? 'captionTitleZh' : 'captionTitle';
            captionTitle.textContent = activeSlide.dataset[captionKey] ?? '';
        }
    };

    languageToggle?.addEventListener('click', () => {
        const nextLanguage = document.documentElement.lang.startsWith('zh') ? 'en' : 'zh';
        applyLanguage(nextLanguage);
    });
    applyLanguage('en');

    const updateHeader = () => {
        header?.classList.toggle('is-scrolled', window.scrollY > 24);
    };

    window.addEventListener('scroll', updateHeader, { passive: true });
    updateHeader();

    menuToggle?.addEventListener('click', () => {
        const isOpen = nav?.classList.toggle('is-open') ?? false;
        menuToggle.setAttribute('aria-expanded', String(isOpen));
    });

    nav?.querySelectorAll('a').forEach((link) => {
        link.addEventListener('click', () => {
            nav.classList.remove('is-open');
            menuToggle?.setAttribute('aria-expanded', 'false');
        });
    });

    if (carousel) {
        const track = carousel.querySelector('.carousel-track');
        const slides = [...carousel.querySelectorAll('.carousel-slide')];
        const dots = [...carousel.querySelectorAll('.carousel-dot')];
        const previous = carousel.querySelector('[data-carousel-prev]');
        const next = carousel.querySelector('[data-carousel-next]');
        const captionNumber = carousel.querySelector('.carousel-caption > span:first-child');
        const captionTitle = carousel.querySelector('[data-carousel-title]');
        let current = 0;
        let timer;

        const showSlide = (index) => {
            current = (index + slides.length) % slides.length;
            if (track) track.style.transform = `translateX(-${current * 100}%)`;
            slides.forEach((slide, slideIndex) => {
                const active = slideIndex === current;
                slide.classList.toggle('is-active', active);
                slide.setAttribute('aria-hidden', String(!active));
            });
            const activeSlide = slides[current];
            if (captionNumber) captionNumber.textContent = String(current + 1).padStart(2, '0');
            const captionKey = document.documentElement.lang.startsWith('zh') ? 'captionTitleZh' : 'captionTitle';
            if (captionTitle) captionTitle.textContent = activeSlide?.dataset[captionKey] ?? '';
            dots.forEach((dot, dotIndex) => {
                const active = dotIndex === current;
                dot.classList.toggle('is-active', active);
                dot.setAttribute('aria-selected', String(active));
            });
        };

        const stopAutoPlay = () => window.clearInterval(timer);
        const startAutoPlay = () => {
            stopAutoPlay();
            timer = window.setInterval(() => showSlide(current + 1), 6000);
        };

        previous?.addEventListener('click', () => { showSlide(current - 1); startAutoPlay(); });
        next?.addEventListener('click', () => { showSlide(current + 1); startAutoPlay(); });
        dots.forEach((dot) => dot.addEventListener('click', () => {
            showSlide(Number(dot.dataset.carouselTo));
            startAutoPlay();
        }));
        carousel.addEventListener('mouseenter', stopAutoPlay);
        carousel.addEventListener('mouseleave', startAutoPlay);
        carousel.addEventListener('focusin', stopAutoPlay);
        carousel.addEventListener('focusout', (event) => {
            if (!carousel.contains(event.relatedTarget)) startAutoPlay();
        });
        showSlide(0);
        startAutoPlay();
    }

    if (!('IntersectionObserver' in window)) {
        revealItems.forEach((item) => item.classList.add('is-visible'));
        return;
    }

    const observer = new IntersectionObserver((entries, currentObserver) => {
        entries.forEach((entry) => {
            if (!entry.isIntersecting) return;
            entry.target.classList.add('is-visible');
            currentObserver.unobserve(entry.target);
        });
    }, { threshold: 0.12, rootMargin: '0px 0px -6% 0px' });

    revealItems.forEach((item) => observer.observe(item));
})();
