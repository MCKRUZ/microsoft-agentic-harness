/**
 * Content for the Capability Showcase page. Plain browser-loaded data — no build step.
 *
 * Shape: Category { id, number, name, subtitle, boxes: Box[] }
 *        Box { id, name, status, exec, eng?, techs?, docLink? }
 * status: 'built' | 'partial' | 'not-built' | 'external'
 *   built    = real, shipped, verified against the actual source tree
 *   partial  = the underlying capability is real, but not packaged as its own named thing
 *   not-built = genuinely absent — no invented mechanism, just an honest label
 *   external = deliberately NOT built here — an outside system this harness connects to
 *
 * Every 'built'/'partial' claim here was checked against src/ (grep, not assumption) before
 * being written. Where a box's own docs go deeper, docLink points at the real onboarding chapter.
 */
window.SHOWCASE_CATEGORIES = [
    {
        id: 'user-surface',
        number: '01',
        name: 'User Surface',
        subtitle: 'Where requests enter the system',
        boxes: [
            {
                id: 'chat-ui',
                name: 'Chat UI',
                status: 'built',
                exec: 'A full conversational web app — message list, tool inspector, agent picker — not a demo widget.',
                eng: 'Presentation.WebUI: React SPA with ChatPanel/MessageList/ChatInput, an MCP tool/prompt/resource browser, and AG-UI streaming via useAgentHub.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A structured question',
                            text: 'Instead of asking the user to type a paragraph of details, the agent asks the browser to render an actual interactive form right inside the conversation.',
                        },
                        {
                            time: 'Before it renders',
                            text: 'The browser checks what the agent actually sent against what that specific form expects. An agent\'s output is something you can\'t fully trust any more than raw user input — it gets checked the same way.',
                        },
                        {
                            time: 'Separately',
                            text: 'A developer working on this harness wants to poke at one specific tool directly, without going through the agent at all. The same web app lets them browse every available tool, prompt, and resource and invoke one by hand to see exactly what it returns.',
                        },
                        {
                            time: 'The whole time',
                            text: 'Responses stream in live, token by token, over a real, open streaming protocol — not a custom polling loop reinventing that wheel.',
                        },
                    ],
                    flow: [
                        { title: 'Stream The Response', tone: 'info', body: 'Text arrives incrementally over a real streaming protocol, not a full-response wait.' },
                        { title: 'Or, Render A Widget', tone: 'jargon', body: 'Instead of plain text, the agent can ask the browser to render a real interactive form, table, or image inline.' },
                        { title: 'Validate Before Trusting It', tone: 'warn', body: 'The agent\'s request is checked against what that specific widget actually expects before anything real gets rendered.' },
                        { title: 'Or, Poke At A Tool Directly', tone: 'tip', body: 'Independent of the agent, a person can browse and manually invoke any available tool to see what it returns.' },
                    ],
                    narrative: [
                        'A "chat UI" for a template like this could easily mean a demo text box. What\'s actually here goes well past that.',
                        'What\'s real: this streams over the <mark class="hl">real, open AG-UI protocol</mark>, not a custom polling loop — and the agent isn\'t limited to plain text. It can ask the browser to render an <mark class="hl">actual interactive form, table, or image</mark> right inline in the conversation. Because that request comes from the agent, and agent output is exactly the kind of thing that can\'t be fully trusted, the browser <mark class="hl">validates the specific arguments against what that widget expects before rendering anything real</mark> — the same discipline normally reserved for user input, applied here to AI output instead.',
                        'It doubles as a genuine developer surface too: every tool, prompt, and resource the agent has access to can be <mark class="hl">browsed and manually invoked by a person</mark>, independent of the agent ever deciding to use it — useful for checking exactly what a tool returns without needing the agent to be the one asking.',
                    ],
                    techTable: {
                        columns: ['Piece', 'What It Does'],
                        rows: [
                            ['Streaming', 'Real-time responses over the open AG-UI protocol, not a bespoke polling loop.'],
                            ['Interactive widgets', 'Agent-triggered forms, tables, and images rendered inline — not just text.'],
                            ['MCP browser', 'Every available tool, prompt, and resource can be browsed and manually invoked, independent of the agent.'],
                            ['Real authentication', 'Microsoft Entra login gates the whole app — not a stubbed identity.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The agent can trigger real UI, not just text',
                            body: 'A form, a table, or an image can be rendered directly in the conversation at the agent\'s request.',
                        },
                        {
                            title: 'Widget arguments are validated at the client',
                            body: 'The agent\'s request is checked against what that specific widget expects before rendering — the same trust-boundary discipline as validating user input, applied to AI output instead.',
                        },
                        {
                            title: 'An oversized payload is handled explicitly, not silently guessed at',
                            body: 'If a widget\'s real arguments are too large to stream, the client is told outright rather than treating an empty placeholder as if it were genuine (empty) input.',
                        },
                        {
                            title: 'A real, open streaming protocol',
                            body: 'Responses stream over AG-UI, an actual open protocol for agent runs — not a homegrown polling or server-sent-events workaround.',
                        },
                        {
                            title: 'Doubles as a tool debugging surface',
                            body: 'A person can manually invoke any MCP tool the agent has access to and inspect the raw result, without the agent needing to be involved at all.',
                        },
                    ],
                    whyItMatters:
                        'A chat box that only shows text caps what an agent can actually do for someone — some answers are genuinely better as a form to fill out or a table to scan than a paragraph to read through. Being able to render real UI from an agent\'s request safely, and giving a person a direct way to poke at the underlying tools, is the difference between a demo chat widget and something a team would actually want to work in every day.',
                },
            },
            {
                id: 'voice',
                name: 'Voice',
                status: 'not-built',
                exec: 'Not built. No speech-to-text or text-to-speech pipeline exists in this repo today.',
            },
            {
                id: 'embedded-copilot',
                name: 'Embedded Copilot',
                status: 'not-built',
                exec: 'Not built as a distinct embeddable widget. The chat UI is a standalone app, not a drop-in component for someone else’s product.',
            },
            {
                id: 'api',
                name: 'API',
                status: 'built',
                exec: 'A real HTTP API for driving the harness programmatically — bundles, workflows, tool discovery.',
                eng: 'Presentation.ExecutionApi, spec-first via bundle-api.yaml, documented in the onboarding guide’s Execution API chapter.',
                docLink: '../17-bundle-api.html',
                deepDive: {
                    scenario: [
                        {
                            time: 'An outside system connects',
                            text: 'A team wants to run their own custom agent through this harness without that agent becoming a permanent part of it. They upload a zip bundle containing their own agent manifest and skills.',
                        },
                        {
                            time: 'Before anything is parsed',
                            text: 'That archive is treated as hostile. It gets checked for the specific tricks a malicious zip file plays — files that escape their extraction folder, absurd compression ratios, symlinks pointing somewhere they shouldn\'t — before a single file inside it is trusted enough to read.',
                        },
                        {
                            time: 'The bundle asks for access',
                            text: 'Its own manifest declares which tools and how much autonomy it wants. That\'s treated as a request, not a grant — what actually gets enforced comes from a completely separate table, resolved from the real caller\'s identity, independent of anything the bundle itself claims.',
                        },
                        {
                            time: 'Someone probes a handle that isn\'t theirs',
                            text: 'Instead of confirming "that exists, but you can\'t touch it," the API responds exactly as if it never existed — a plain 404, indistinguishable from a handle that was never created.',
                        },
                    ],
                    flow: [
                        { title: 'Treat Upload As Hostile', tone: 'warn', body: 'The archive is checked against real attack patterns — path escapes, compression bombs, bad symlinks — before anything inside it is parsed.' },
                        { title: 'Request, Don\'t Grant', tone: 'jargon', body: 'A bundle\'s own manifest says what it wants; a separate, authoritative table decides what it actually gets, based on who\'s really calling.' },
                        { title: 'One Execution Path, However It\'s Triggered', tone: 'info', body: 'Whether a run is queued in the background or streamed live, it goes through the exact same gate — nothing gets a second, less-guarded route.' },
                        { title: 'Fail Closed, Never Open', tone: 'tip', body: 'An unrecognized caller, a missing setting, or a value that fails to parse all resolve to the most restrictive outcome, never the most permissive.' },
                    ],
                    narrative: [
                        'This API is a way for an outside system to run <mark class="hl">an agent the harness itself never wrote</mark> — safely — not just a REST wrapper around the same conversations a logged-in chat user already has.',
                        'What\'s real: uploaded content is handled as <mark class="hl">genuinely hostile input before it\'s ever parsed</mark>, checked against real archive-attack patterns rather than just "did the zip open." What a bundle\'s own manifest asks for is <mark class="hl">never trusted as the actual grant</mark> — a separate, authoritative permission table resolved from the real caller\'s identity decides what\'s actually enforced, and it\'s built to <mark class="hl">fail toward the most restrictive outcome</mark> whenever something is missing or ambiguous, never the most permissive. And an unauthorized attempt to touch someone else\'s bundle gets a response <mark class="hl">deliberately indistinguishable from that bundle never existing at all</mark>, so the API itself can\'t be used to confirm anyone else\'s private handles even exist.',
                        'One more subtlety worth calling out: two different ways exist to trigger a run — queued in the background, or streamed live to a connected caller — and both are forced through <mark class="hl">the identical execution path</mark> specifically so the security enforcement can\'t quietly drift apart between them over time.',
                    ],
                    techTable: {
                        columns: ['Guard', 'What It Prevents'],
                        rows: [
                            ['Archive shape validation', 'A malicious zip designed to blow up on extraction or escape its target folder.'],
                            ['Capability envelope', 'A bundle\'s own manifest claiming more access than its real caller is actually entitled to.'],
                            ['Non-disclosing ownership checks', 'Confirming to an attacker that someone else\'s private bundle handle even exists.'],
                            ['One shared execution path', 'Background and live-streamed runs silently drifting into different security behavior over time.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'A bundle\'s permissions are a request, never a grant',
                            body: 'What it self-declares in its manifest is checked against a separate, authoritative table — the manifest never grants itself anything.',
                        },
                        {
                            title: 'Fails toward the strictest outcome everywhere',
                            body: 'An unmatched caller, missing configuration, or a value that fails to parse all resolve to no access rather than defaulting open.',
                        },
                        {
                            title: 'Guards against specific, real archive attacks',
                            body: 'Zip-slip path escapes, escaping symlinks, and disproportionate compression ratios are all checked — not just a basic file-size limit.',
                        },
                        {
                            title: 'Ownership checks never disclose existence',
                            body: 'Reading or deleting someone else\'s handle looks exactly like acting on one that was never created — never a confirming "found but forbidden."',
                        },
                        {
                            title: 'Background and live runs share one execution gate on purpose',
                            body: 'Both trigger paths are forced through the same executor specifically so their security enforcement can\'t quietly diverge over time.',
                        },
                    ],
                    whyItMatters:
                        'Letting an outside system run code your own harness didn\'t write is one of the riskiest things a platform like this can offer — the same shape of problem as running someone else\'s plugin. The difference between that being a real capability and a real liability is exactly this level of care: treating uploaded content as hostile by default, never trusting what it claims about itself, and closing every gap where two enforcement paths could quietly drift apart.',
                },
            },
            {
                id: 'a2a-inbound',
                name: 'A2A Inbound',
                status: 'built',
                exec: 'Other AI agents can call into this harness using the open Agent-to-Agent protocol, with identity carried through, not dropped.',
                eng: 'Infrastructure.AI/A2A/A2AIdentityPropagator.cs propagates caller identity across an inbound A2A call; backed by Domain.AI/Identity/AgentIdentity.cs.',
                deepDive: {
                    scenario: [
                        {
                            time: 'An outside agent calls in',
                            text: 'A completely separate AI system, built by someone else, calls into this harness using the open Agent-to-Agent protocol — a standard way for different agent systems to talk to each other.',
                        },
                        {
                            time: 'It claims an identity',
                            text: 'The incoming message says who it\'s from and what kind of caller it is. None of that is simply believed — the harness independently confirms who\'s really calling through its own authentication layer.',
                        },
                        {
                            time: 'What gets trusted, and what doesn\'t',
                            text: 'The caller\'s self-declared identity in the message is never used as the actual identity — only what the harness\'s own auth layer confirmed is. The message\'s claim about what "kind" of caller it is IS used, but carefully: an unrecognized value can\'t accidentally look legitimate.',
                        },
                        {
                            time: 'For the rest of the call',
                            text: 'Every tool, skill, and audit log downstream sees the real, established caller — not a generic "external agent" placeholder every outside caller would otherwise look identical as.',
                        },
                    ],
                    flow: [
                        { title: 'Receive The Envelope', tone: 'info', body: 'A message arrives over the open, standard protocol other agent systems already use — nothing invented just for this harness.' },
                        { title: 'Authenticate, Don\'t Trust The Message', tone: 'warn', body: 'Who\'s actually calling is established through the harness\'s own authentication layer, never taken from what the message itself claims.' },
                        { title: 'Carry Identity Through The Call', tone: 'jargon', body: 'Once established, that identity becomes the ambient identity for the whole call — every tool and skill downstream sees the real caller.' },
                        { title: 'Default To The Safe Reading', tone: 'tip', body: 'Anything taken from the message itself is parsed defensively, so a malformed or unrecognized value can\'t accidentally look authorized.' },
                    ],
                    narrative: [
                        'A2A is an open, standardized way for one AI agent system to call into another — not a bespoke integration invented just for this harness.',
                        'What\'s real: an inbound call genuinely carries the caller\'s identity through the whole interaction, so tools, skills, and audit logging downstream all see who\'s really calling — not a shared placeholder every outside agent looks identical as. The interesting detail is how carefully that identity is established: the caller\'s id is <mark class="hl">never taken from what the message itself claims</mark> — it\'s the identity the harness\'s own authentication layer already confirmed, independent of anything on the wire. A more permissive field, what "kind" of caller it claims to be, is taken from the message — but <mark class="hl">defensively</mark>: an unrecognized or malformed value doesn\'t get the benefit of the doubt, it falls back to the exact state every downstream check already treats as "nothing established," so it\'s denied by default rather than accidentally granted.',
                        'This is the same discipline showing up again elsewhere on this page: <mark class="hl">whatever a caller claims about itself is something to verify, never a fact to trust outright</mark>.',
                    ],
                    techTable: {
                        columns: ['Field', 'Where It Comes From', 'Trusted?'],
                        rows: [
                            ['Caller identity (who)', 'The harness\'s own authentication layer', 'Always — never taken from the message itself.'],
                            ['Caller kind (what type)', 'The inbound message', 'Yes, but defensively — an unrecognized value denies rather than defaulting to authorized.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The caller id is never read from the message',
                            body: 'Only what the harness\'s own authentication layer independently confirmed is used — a hostile message can\'t simply claim to be someone else.',
                        },
                        {
                            title: 'An unrecognized "kind" value fails closed, not ambiguously',
                            body: 'A naive parse could land a malformed value somewhere that\'s neither the legitimate state nor the deny-by-default one, and get read as authorized by accident — this explicitly avoids that gap.',
                        },
                        {
                            title: 'Case-insensitive matching was a deliberate, documented change',
                            body: 'Made consistent with how every other governance value in this codebase is read, closing a case where one comparison was silently stricter than the rest.',
                        },
                        {
                            title: 'Identity persists for the whole call, not just at the door',
                            body: 'Once established, it becomes the ambient identity for the duration of the call, so every downstream tool or skill invocation sees the real caller.',
                        },
                        {
                            title: 'The same discipline applies in reverse, outbound',
                            body: 'This harness refuses to make an outbound A2A call at all without a clear identity of its own to stamp on the envelope, rather than sending one anonymously.',
                        },
                    ],
                    whyItMatters:
                        'An open standard for agents calling other agents is only as trustworthy as how carefully each side verifies what the other side claims about itself. Treating an inbound caller\'s own self-description as a claim to verify, not a fact to trust, is exactly what keeps "any AI system can call into this one" from also meaning "any AI system can pretend to be anyone it wants."',
                },
            },
        ],
    },
    {
        id: 'identity-trust',
        number: '02',
        name: 'Identity & Trust',
        subtitle: 'Who is asking, what are they allowed to do',
        boxes: [
            {
                id: 'sso-oauth',
                name: 'SSO / OAuth',
                status: 'built',
                exec: 'Real login, not a stub — requests are authenticated before anything else happens.',
                eng: 'JWT Bearer authentication via Microsoft Entra ID, wired through Presentation.Common, Presentation.ExecutionApi, and Presentation.AgentHub.',
                deepDive: {
                    scenario: [
                        {
                            time: 'No token, or an old one',
                            text: 'A request arrives with no credentials, or an expired one. It\'s rejected before it ever reaches any real business logic — authentication is the very first gate, not something bolted onto individual endpoints later.',
                        },
                        {
                            time: 'A developer working locally',
                            text: 'Doesn\'t want to stand up a real Entra tenant just to test a feature. A dev-only bypass exists that auto-authenticates every request as a synthetic user — but it\'s guarded twice over so it can\'t quietly end up active somewhere real.',
                        },
                        {
                            time: 'A token expires',
                            text: 'Most auth libraries build in a five-minute grace window by default, so a slightly-off clock doesn\'t cause spurious failures. This system deliberately turns that off — an expired token is rejected exactly when it says it should be, not five minutes later.',
                        },
                        {
                            time: 'A live connection opens',
                            text: 'A browser opens a persistent, real-time connection for streamed agent updates. That kind of connection can\'t carry a normal authorization header, so its token has to travel a different way — but only for that one specific kind of connection.',
                        },
                    ],
                    flow: [
                        { title: 'Reject Before Anything Else Runs', tone: 'info', body: 'A request without valid credentials never reaches business logic at all.' },
                        { title: 'Double-Guard The Escape Hatch', tone: 'warn', body: 'A local-dev bypass exists, but only activates when two separate conditions are both explicitly true.' },
                        { title: 'Tighten What The Library Leaves Loose', tone: 'jargon', body: 'The underlying auth library\'s own default grace window on token expiry is deliberately overridden to be stricter.' },
                        { title: 'One Narrow, Precisely-Scoped Exception', tone: 'tip', body: 'A real-time connection gets its token a different way — but only for that connection type, nothing else.' },
                    ],
                    narrative: [
                        '"Real login" here means requests are authenticated as the very first thing that happens, against an actual identity provider — not a stub, and not a header anyone could forge.',
                        'What\'s real, and one interesting nuance: there genuinely is a local-development bypass that skips real login entirely — but it\'s <mark class="hl">deliberately double-guarded</mark> so it can\'t quietly end up active anywhere real. It only turns on when the environment is genuinely Development <mark class="hl">and</mark> a separate config flag is explicitly turned on — neither alone is enough. There\'s also a deliberate hardening past the library\'s own defaults: the underlying auth library leaves a five-minute grace window on token expiry by default, and this system <mark class="hl">turns that off entirely</mark>, per its own documented security standard — an expired token is rejected exactly when it expires, not five minutes later.',
                        'One precisely-scoped exception exists for real-time connections, which can\'t carry a normal authorization header: their token travels a different way, but that alternate path is <mark class="hl">checked against the specific connection type</mark>, not loosened for every request across the board.',
                    ],
                    techTable: {
                        columns: ['Setting', 'Library Default', 'This System'],
                        rows: [
                            ['Token clock skew', 'A five-minute grace window', 'Zero — rejected exactly at expiry, per project security standard.'],
                            ['Dev auth bypass', 'Not applicable', 'Off by default; requires two separate conditions both true to enable.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The dev bypass needs two conditions, not one',
                            body: 'Genuinely being in a Development environment isn\'t enough on its own — a separate config flag must also be explicitly turned on.',
                        },
                        {
                            title: 'Clock skew is deliberately zeroed against the library default',
                            body: 'A five-minute grace window most auth setups leave in place is turned off on purpose, so an expired token is rejected exactly on schedule.',
                        },
                        {
                            title: 'The streaming-connection token exception is narrowly scoped',
                            body: 'The alternate way of reading a token only applies to that specific connection path, and existing authentication event handlers are chained onto — not replaced — so nothing else quietly stops firing.',
                        },
                        {
                            title: 'A second, unused authentication path exists in this codebase',
                            body: 'A more generic setup lives in the shared Presentation layer with no real caller outside its own tests — the auth that\'s actually live in the running hosts is wired directly, per host, using a different, more current library.',
                        },
                    ],
                    whyItMatters:
                        'Authentication is supposed to fail loudly and predictably, every time — a local convenience that could accidentally activate somewhere real, or a grace window that quietly lets an expired credential through, are exactly the kind of small gaps that become real incidents. Guarding the escape hatch twice over and tightening a library\'s own default leniency are the unglamorous details that separate "we have login" from "our login is actually trustworthy."',
                },
            },
            {
                id: 'agent-identity',
                name: 'Agent Identity',
                status: 'built',
                exec: 'The agent itself has a verifiable identity, distinct from the human or system that invoked it — needed the moment one agent calls another.',
                eng: 'Domain.AI/Identity/AgentIdentity.cs, consumed by the A2A identity propagator on both inbound and outbound calls.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A tool call that matters',
                            text: 'An agent calls a tool that could touch a real cloud resource. Before it goes through, the system checks two separate things: who is this ultimately being done for, and which specific agent is actually making the call right now.',
                        },
                        {
                            time: 'One agent calls another',
                            text: 'The second agent needs its own answer to "who am I" — one that\'s completely independent of whatever the first agent\'s own text claims about itself.',
                        },
                        {
                            time: 'A clever attempt',
                            text: 'Someone crafts a prompt that convinces an agent to claim, in its own output, that it\'s actually a more privileged agent. That claim never gets anywhere near the real permission check — a piece of text inside a conversation can\'t talk its way into a runtime identity.',
                        },
                        {
                            time: 'Bound to something real',
                            text: 'This same identity can be tied to an actual cloud service principal — meaning the agent itself, not just the human behind it, can be individually granted or denied access to real infrastructure.',
                        },
                    ],
                    flow: [
                        { title: 'Establish The Agent\'s Own Identity', tone: 'info', body: 'Separate and independent from whatever human or system triggered this chain of work.' },
                        { title: 'Check It Alongside The Human\'s', tone: 'jargon', body: 'Both the human-caller identity and the agent\'s own runtime identity are checked, and both have to pass.' },
                        { title: 'Bind It To Something Real', tone: 'tip', body: 'The identity can be tied to an actual cloud service principal, with its own real access grants.' },
                        { title: 'Validate At The Gate, Not At Creation', tone: 'warn', body: 'An identity record floating around isn\'t trusted just because it exists — it only becomes trustworthy once it\'s passed the real check.' },
                    ],
                    narrative: [
                        'A system prompt telling an agent to "act carefully" is just text — it degrades the moment someone crafts input clever enough to talk around it. This gives an agent a runtime identity built differently: <mark class="hl">not something the agent\'s own output can influence at all</mark>.',
                        'What\'s real: this identity is checked <mark class="hl">completely independently from, and in addition to, the human-caller identity</mark> — "who is this being done for" and "which specific agent is doing it right now" are two separate questions, and both have to come back yes. It\'s not just an internal label either — it can be bound to a <mark class="hl">real cloud identity with real access-control role assignments</mark>, so the agent itself can be individually granted or denied access the same way a real employee\'s account would be.',
                        'One honest, deliberate design point: the feature that enforces this is opt-in, and turning it off doesn\'t degrade gracefully — it means <mark class="hl">none of this gets checked at all</mark>. That\'s a real, load-bearing security control, and this codebase specifically has an automated test whose entire job is catching the exact failure mode of a control like this existing and being tested, but quietly never getting wired into anything that actually calls it — a mistake this repo has documented making before.',
                    ],
                    techTable: {
                        columns: ['Question', 'Answered By'],
                        rows: [
                            ['Who is this work ultimately for?', 'The human-caller scope.'],
                            ['Which specific agent is doing the work right now?', 'Agent Identity — checked independently, both required.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Two independent checks, both required',
                            body: 'The human-caller scope and the agent\'s own runtime identity are separate questions with separate answers, and both must pass.',
                        },
                        {
                            title: 'Immune to what the agent says about itself',
                            body: 'Runtime identity isn\'t derived from anything inside a conversation, so a prompt injection convincing the agent to claim a different identity has nowhere to attach.',
                        },
                        {
                            title: 'Can be bound to a real cloud principal',
                            body: 'A genuine cloud-registered identity, capable of carrying real access-control role assignments — not just an internal bookkeeping label.',
                        },
                        {
                            title: 'Validated at the gate, not on creation',
                            body: 'The identity record itself accepts any values when constructed; a dedicated authorization gate enforces the real rules, so anything obtained elsewhere should be treated as unverified until it actually passes that gate.',
                        },
                        {
                            title: 'Opt-in, with a dedicated test to catch it going dark',
                            body: 'Enforcement only runs when explicitly turned on, and this codebase has automated tests specifically confirming a security control like this one has a real caller — not just passing tests of its own.',
                        },
                    ],
                    whyItMatters:
                        'The moment one agent can call another, or an agent can touch real infrastructure, "who is actually doing this" stops being a rhetorical question. A runtime identity that a clever prompt can\'t talk its way into, checked independently of and in addition to who the human is, is what keeps "which agent did this" from ever being answerable only by trusting the agent\'s own account of itself.',
                },
            },
            {
                id: 'scoped-credentials',
                name: 'Scoped Credentials',
                status: 'partial',
                exec: 'Real, but not packaged as its own product — it lives inside the egress and tool-governance layer rather than as a standalone credential-scoping service.',
                eng: 'Enforced via SkillManifestEgressPolicyResolver and the tool-invocation governance chain, not a dedicated credentials-scoping component.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A skill needs one thing',
                            text: 'A skill\'s job legitimately requires calling one specific external API. Its manifest declares that host as an addition to the shared baseline — it can only ever widen what it\'s allowed to reach, never narrow another skill\'s access or override the shared default.',
                        },
                        {
                            time: 'The call goes out',
                            text: 'Before any name even resolves, the destination is checked against the allowlist for the specific skill actually running right now — not a single static setting shared by everything.',
                        },
                        {
                            time: 'A known trick',
                            text: 'Suppose a hostname is designed to resolve to something disallowed at the exact moment the real connection is made — a well-known way to slip past a check that only looks at the hostname. A second, independent layer checks the actual address at the moment of connecting, closing that gap.',
                        },
                        {
                            time: 'No one to attribute it to',
                            text: 'A background job with no attributable human or agent behind it tries an outbound call outside any real work in progress. It\'s refused outright — an unattributed call gets no benefit of the doubt.',
                        },
                    ],
                    flow: [
                        { title: 'Declare Additively, Never Override', tone: 'info', body: 'A skill can only widen its own outbound reach beyond the shared default — never narrow someone else\'s or replace the baseline.' },
                        { title: 'Check The Hostname First', tone: 'jargon', body: 'The declared allowlist is checked before any name resolution happens.' },
                        { title: 'Check The Real Connection Second', tone: 'warn', body: 'A separate, independent layer filters at the actual point of connection, closing a gap a hostname-only check can\'t.' },
                        { title: 'No Identity, No Verdict', tone: 'tip', body: 'A call with no attributable identity behind it is refused by default, not waved through.' },
                    ],
                    narrative: [
                        'This isn\'t a system for issuing and rotating API keys — it\'s a way of scoping which external destinations a given piece of running code is even allowed to reach.',
                        'What\'s real, and genuinely well-built: every skill\'s allowlist is <mark class="hl">purely additive</mark> — a skill can widen the shared baseline for its own outbound calls, but can never narrow it or override what another skill is allowed. Enforcement is <mark class="hl">two independent layers, not one</mark>: a declared hostname allowlist checked before any name resolution happens, plus a separate check at the moment the real network connection is made — specifically closing a well-known trick where a hostname resolves to something different than what was checked a moment earlier. And a call with <mark class="hl">no attributable identity behind it gets refused outright</mark>, because an unattributable call can\'t be meaningfully audited or scoped in the first place.',
                        'Why "partial" is honest here, not just modest: this genuinely controls what a skill can reach, but it isn\'t packaged as its own standalone product someone could point to and configure in isolation — it\'s <mark class="hl">one governance layer among several</mark>, and the label reflects that packaging reality, not a gap in the actual protection.',
                    ],
                    techTable: {
                        columns: ['Layer', 'What It Checks', 'When'],
                        rows: [
                            ['Declared allowlist', 'The requested hostname against what this specific skill is permitted to reach.', 'Before any name resolution happens.'],
                            ['Connection-time filter', 'The actual address the connection lands on.', 'At the moment of connecting.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Allowlists are additive only',
                            body: 'A skill can widen its own outbound reach beyond the shared default; it can never narrow it or override another skill\'s.',
                        },
                        {
                            title: 'Two independent layers, closing a real bypass',
                            body: 'A hostname check before resolution, plus a separate connection-time address filter — specifically closing a known trick the first layer alone can\'t catch.',
                        },
                        {
                            title: 'Scoped by skill, gated by identity — deliberately not mixed',
                            body: 'Who\'s calling controls whether a call can happen at all; which skill is running controls which hosts that call can reach — two separate questions, kept separate on purpose.',
                        },
                        {
                            title: 'No attributable identity means no verdict at all',
                            body: 'Background work with nothing to attribute a call to is refused by default rather than falling back to a permissive default.',
                        },
                        {
                            title: 'Every decision is audited, allow or deny',
                            body: 'The outcome is written to an audit trail regardless of verdict — not just the interesting failures.',
                        },
                    ],
                    whyItMatters:
                        'Giving an AI agent the ability to call out to the internet is exactly the kind of capability that turns a clever prompt into a real data-exfiltration or server-side-request-forgery attempt if it isn\'t scoped tightly. Layering a declared allowlist with an independent connection-time check, and refusing calls with no attributable identity at all, is what makes "the agent can reach the network" survive contact with someone actually trying to abuse it.',
                },
            },
            {
                id: 'policy-engine',
                name: 'Policy Engine',
                status: 'partial',
                exec: 'Policy decisions are real and enforced, but there’s no single named "policy engine" — the logic lives inside the governance layer described under Governance & Control.',
                eng: 'ToolInvocationGovernor and the surrounding governance chain make the actual policy decisions at tool-call time.',
                deepDive: {
                    scenario: [
                        {
                            time: 'An agent wants to call a tool',
                            text: 'Several independent checks all have a say: what this agent is generally permitted to do, whether its current risk tier tightens that, whether the sandbox it\'s running in structurally supports this at all, and a separate written policy layer.',
                        },
                        {
                            time: 'One check would deny it outright',
                            text: 'That check runs first, before any human is ever paged — nobody gets interrupted for a decision the automatic checks were always going to reach on their own.',
                        },
                        {
                            time: 'Two checks, two different reasons',
                            text: 'Two completely different gates, for two completely different reasons, both want a human to weigh in on the same call. Instead of asking twice — or asking once and hiding the second reason — one question is asked that names everything actually being decided.',
                        },
                        {
                            time: 'The human says yes',
                            text: 'That approval settles exactly what was asked. It can\'t be silently read as also clearing some other gate\'s separate concern that happened to be pending at the same time.',
                        },
                    ],
                    flow: [
                        { title: 'Run Every Automatic Check First', tone: 'info', body: 'Permission, risk tier, sandbox capability, and written policy all get their say before a human is ever involved.' },
                        { title: 'Only Ask If Something Still Needs A Person', tone: 'jargon', body: 'If every automatic check clears the call, nothing gets escalated at all.' },
                        { title: 'Ask One Question, Not Several', tone: 'warn', body: 'If more than one gate wants a human for different reasons, those reasons are combined into a single, complete question.' },
                        { title: 'Approval Answers Exactly What Was Asked', tone: 'tip', body: 'A "yes" can never be read as also clearing a reason the approver was never shown.' },
                    ],
                    narrative: [
                        'This is a real, enforced decision process for whether an agent\'s tool call is allowed to happen — built after discovering that a whole separate set of governance checks, already written for a different call path, simply never ran for the agent\'s own live tool calls.',
                        'What\'s real: <mark class="hl">four distinct, independently-authored checks</mark> — permission, risk-adjusted permission, sandbox capability, and a written policy layer — all run automatically, in a specific, deliberate order. A human only gets paged once every automatic check has already had its say, and only for the specific thing none of them could resolve on their own.',
                        'The genuinely hard-won detail: if two different checks want a human for two different reasons, the system doesn\'t ask twice, and it doesn\'t ask once while hiding the second reason — it <mark class="hl">combines them into one question that shows everything actually being decided</mark>. That detail exists because getting it wrong is silent, and this exact codebase got it wrong twice before landing on the current design — once by letting an approval bypass a check it was never meant to touch, once by letting an approval for one reason quietly clear a second reason the approver never saw.',
                    ],
                    techTable: {
                        columns: ['Gate', 'What It Checks'],
                        rows: [
                            ['Permission', 'What this specific agent is broadly allowed to do.'],
                            ['Risk-adjusted permission', 'Whether the agent\'s current risk tier tightens that further — never loosens it.'],
                            ['Sandbox capability', 'Whether the environment the agent is running in structurally supports this at all.'],
                            ['Declarative policy', 'A separate, written set of rules that can still object even when everything else allows it.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Deterministic checks run before any human is asked',
                            body: 'Specifically so a human isn\'t paged for a call the automatic checks were always going to deny anyway.',
                        },
                        {
                            title: 'Multiple pending reasons get combined into one question',
                            body: 'A human is never shown only the first of several reasons a call needs sign-off.',
                        },
                        {
                            title: 'A risk-tier check can only tighten a decision, never loosen one',
                            body: 'Already-denied access can\'t be granted back by a risk recalculation.',
                        },
                        {
                            title: 'The whole decision reads one config snapshot, not two',
                            body: 'So a live settings reload can\'t land mid-decision and judge half the call under old rules and half under new ones.',
                        },
                        {
                            title: 'The audit trail can\'t contradict itself',
                            body: 'A block can never be recorded as "the approver said no" for a call the approver actually approved — nothing can override an approval after the fact.',
                        },
                    ],
                    whyItMatters:
                        'A policy system that can be silently bypassed by an approval meant for something else, or that pages a human for a decision that was already settled, isn\'t actually a safety control — it\'s a false sense of one. Getting the order right, combining every reason into one honest question, and making the audit trail incapable of contradicting itself is what makes "an agent\'s tool calls are governed" something you can actually rely on instead of something that only looks that way until it\'s tested.',
                },
            },
        ],
    },
    {
        id: 'orchestration',
        number: '03',
        name: 'Orchestration',
        subtitle: 'Decides what happens, in what order, by whom',
        boxes: [
            {
                id: 'classifier',
                name: 'Classifier',
                status: 'partial',
                exec: 'A real classifier exists and is genuinely shared — it scores task complexity for both model-tier routing and retrieval routing — but it only classifies one dimension (how hard is this), not general request-type or intent classification.',
                eng: 'TaskComplexityClassifier (LLM-based, four tiers: trivial/simple/moderate/complex) is consumed by both ModelRouter and RagOrchestrator — one shared classifier, not duplicated per caller.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A one-word greeting',
                            text: 'Before anything expensive happens, the system recognizes this needs almost nothing and routes it to the cheapest available model — no wasted spend on a trivial request.',
                        },
                        {
                            time: 'A genuinely hard question',
                            text: 'Something needing deep, multi-step reasoning across several sources gets recognized as complex and routed to a more capable — and more expensive — model instead.',
                        },
                        {
                            time: 'The same signal, a different decision',
                            text: 'Separately, using the exact same classification, a retrieval question judged complex enough gets a more thorough, multi-round search instead of a single quick lookup.',
                        },
                        {
                            time: 'Most of the time',
                            text: 'None of this costs an extra model call at all — a cheaper heuristic is tried first, and the real classifier here only gets invoked when that heuristic isn\'t confident enough to decide on its own.',
                        },
                    ],
                    flow: [
                        { title: 'Try The Cheap Path First', tone: 'info', body: 'A lightweight heuristic attempts to judge complexity without an extra model call.' },
                        { title: 'Fall Back Only When Unsure', tone: 'jargon', body: 'An actual model call happens only when that heuristic isn\'t confident, using the cheapest available tier.' },
                        { title: 'Classify Into Four Tiers', tone: 'tip', body: 'Trivial, simple, moderate, or complex — each defined with concrete examples of what belongs where.' },
                        { title: 'Feed Two Different Decisions', tone: 'warn', body: 'The same result drives which model tier handles a request and how thorough a retrieval pass needs to be.' },
                    ],
                    narrative: [
                        'What\'s real, and a genuine correction from what this box used to say: the classifier isn\'t a one-off tucked inside retrieval routing — <mark class="hl">the same shared classifier also decides which cost tier of model handles an ordinary request</mark>, so a trivial greeting and a genuinely hard reasoning task don\'t cost the same amount to answer.',
                        'What keeps it "partial": it only classifies <mark class="hl">one specific dimension — how complex is this</mark> — not general-purpose intent or request-type classification (what kind of task is this, which skill should own it). A classifier in the fuller sense this taxonomy implies would cover more ground than complexity alone.',
                        'A real cost-conscious detail: it doesn\'t run a model call on every single request. A <mark class="hl">cheaper heuristic is tried first</mark>, and the actual classification here fires only when that heuristic isn\'t confident — spending the extra latency and token cost only on the requests that actually need it.',
                    ],
                    techTable: {
                        columns: ['Tier', 'What It Looks Like'],
                        rows: [
                            ['Trivial', 'Greetings, acknowledgments, simple lookups.'],
                            ['Simple', 'One tool, straightforward Q&A.'],
                            ['Moderate', 'Multiple tools, synthesis across sources.'],
                            ['Complex', 'Deep multi-step reasoning, planning, architecture-level work.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Genuinely shared across two real systems',
                            body: 'Both general model-tier routing and RAG retrieval routing consume the exact same classifier — not two separate copies.',
                        },
                        {
                            title: 'A cheap heuristic runs first',
                            body: 'The model-based classification is explicitly a fallback for when that heuristic isn\'t confident, not the default path for every request.',
                        },
                        {
                            title: 'Fails to a safe middle tier, not an extreme',
                            body: 'A classification failure defaults to "Moderate" — not the cheapest tier, which could starve a hard request of the right model, and not the most expensive one, which could waste money on a trivial one.',
                        },
                        {
                            title: 'It replaced an earlier, narrower interface',
                            body: 'Its own documentation notes it explicitly supersedes a prior query-only complexity classifier — a sign of real iteration, not a first draft left in place.',
                        },
                        {
                            title: 'Only classifies complexity, not intent',
                            body: 'A genuinely different, harder problem — what is this request, not just how hard is it — that this component doesn\'t attempt.',
                        },
                    ],
                    whyItMatters:
                        'Routing every request through the same expensive model regardless of how hard it actually is wastes money on easy questions and can under-serve hard ones. A shared, reused complexity signal that only spends the cost of a model call when a cheap heuristic can\'t decide on its own is a genuinely good design — the honest gap is that "how complex" is a narrower question than "what kind of request is this," and this component only answers the first one.',
                },
            },
            {
                id: 'router',
                name: 'Router',
                status: 'partial',
                exec: 'A real, general-purpose model router exists and is on by default for actual conversation turns — not scoped to retrieval — but it routes which model handles a call, not which skill or agent should own the task in the first place.',
                eng: 'ModelRouter dynamically picks a cost-ordered model tier per turn (complexity-classified, with per-conversation escalation on repeated bad outcomes); a separate request/skill router doesn\'t exist as its own component.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A new conversation starts',
                            text: 'Its first message is straightforward, so the router picks a cheap, fast model for it.',
                        },
                        {
                            time: 'Several turns later',
                            text: 'That same conversation starts producing signs of a poor outcome, turn after turn. Without anyone touching a setting, the router quietly upgrades that specific conversation to a more capable model — not every conversation, just the one that\'s been struggling.',
                        },
                        {
                            time: 'Meanwhile, elsewhere',
                            text: 'A completely fixed, well-understood background task — pulling structured facts out of a finished turn — always uses the same cheap tier, by config, because it doesn\'t need a smart per-request decision at all.',
                        },
                        {
                            time: 'If none of this is wanted',
                            text: 'The dynamic routing can be turned off entirely — when it is, everything runs on one default tier, exactly as if none of this existed.',
                        },
                    ],
                    flow: [
                        { title: 'Classify The Turn', tone: 'info', body: 'A cheap heuristic tries first; an LLM-based fallback runs only when it\'s not confident.' },
                        { title: 'Pick A Base Tier From That', tone: 'jargon', body: 'Trivial or simple work goes to the cheapest tier, complex work to a more capable one.' },
                        { title: 'Check This Conversation\'s Own Track Record', tone: 'warn', body: 'If this specific conversation has been struggling, the tier gets bumped up further — independent of what a fresh conversation with the same message would get.' },
                        { title: 'Some Work Skips All Of This', tone: 'tip', body: 'A handful of fixed, well-understood background tasks route to a config-set tier directly, because a "smart" decision would just be overhead.' },
                    ],
                    narrative: [
                        'A genuine correction to what this box used to say: this isn\'t scoped to retrieval at all — it\'s the harness\'s real, general-purpose model router, and it\'s <mark class="hl">on by default for actual conversation turns</mark>, not something living only inside the RAG pipeline.',
                        'What\'s real: every ordinary turn gets classified for complexity and routed to a cost-ordered tier based on that. On top of that, there\'s a genuinely adaptive layer most systems like this don\'t have: <mark class="hl">each conversation has its own quality track record</mark>, and one racking up consecutive bad outcomes gets automatically escalated to a more capable tier — specifically for that conversation, and capped so it can\'t run away unboundedly.',
                        'Why it\'s still marked <mark class="hl">"partial"</mark>: what\'s real here is dynamic <em>model</em> routing — which provider or tier answers a given call. What doesn\'t exist as its own thing is a general <em>request</em> router that decides which skill or agent should even handle a task in the first place — that responsibility is spread across the Orchestrator and Supervisor Agent rather than consolidated into something you could point to and call "the router."',
                    ],
                    techTable: {
                        columns: ['Path', 'How It Picks A Tier'],
                        rows: [
                            ['An ordinary conversation turn', 'Complexity classification, mapped to a cost-ordered tier.'],
                            ['A conversation with a bad track record', 'The base tier, automatically bumped further based on recent negative outcomes.'],
                            ['A fixed background task (e.g. fact extraction)', 'A single config-set tier — no per-request decision at all.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'On by default for real conversation turns',
                            body: 'Dynamic, complexity-based routing isn\'t an opt-in experiment — it\'s the default path, with a single config flag to disable it and fall back to one static tier.',
                        },
                        {
                            title: 'A conversation\'s own history can override its base tier',
                            body: 'Up to two escalation levels, tracked per conversation, triggered by consecutive negative outcomes — not a one-size-fits-all setting.',
                        },
                        {
                            title: 'Escalation is capped, not unbounded',
                            body: 'A struggling conversation can climb tiers, but only so far, so a genuinely broken loop can\'t silently spiral to the most expensive tier forever.',
                        },
                        {
                            title: 'Fixed background tasks deliberately skip the smart path',
                            body: 'A known-cheap task like fact extraction routes through a simple config override, because paying for a classification decision on every background call would be pure overhead.',
                        },
                        {
                            title: 'Genuinely shared with the Classifier',
                            body: 'The same complexity signal used for retrieval routing also feeds this general model router — they aren\'t two separate implementations.',
                        },
                    ],
                    whyItMatters:
                        'Real conversations don\'t all cost the same to run well, and the cheap model handling the first nine turns fine might start failing on the tenth. A router that not only picks a sensible starting tier but notices when a specific conversation isn\'t going well and adapts for that conversation alone is meaningfully more useful than a static per-operation setting — the honest gap is that this solves "which model," not "which skill or agent," a genuinely separate problem this taxonomy expects a router to also cover.',
                },
            },
            {
                id: 'planner',
                name: 'Planner',
                status: 'built',
                exec: 'A real planning engine that breaks work into a dependency graph and runs it with bounded concurrency, checkpointing, and error recovery.',
                eng: 'PlanExecutor orchestrates a PlanGraph via keyed step executors (LlmCall, ToolUse, HumanGate, ConditionalBranch, SubPlanInvocation), with retry/escalate/skip recovery and EF Core-backed checkpoint/resume.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Several steps, real dependencies',
                            text: 'A request actually needs multiple steps, some of which can run at the same time and some of which have to wait on each other\'s results. Independent steps run concurrently — but never more than a set number at once.',
                        },
                        {
                            time: 'A plan within a plan',
                            text: 'One step is itself "run an entire other plan." The system tracks how deep that nesting goes, and if a plan tries to nest too many levels deep, it\'s refused outright rather than recursing until something breaks.',
                        },
                        {
                            time: 'A restriction that follows the nesting',
                            text: 'The parent plan was explicitly denied access to a particular tool. The nested sub-plan it kicks off inherits that exact same restriction automatically — it can\'t quietly regain access to something its parent was denied just by being one level removed.',
                        },
                        {
                            time: 'When nothing is moving',
                            text: 'The scheduler notices that work is technically still "pending" but nothing is actually progressing. Rather than spinning forever, it logs a warning and stops instead of hanging the whole plan indefinitely.',
                        },
                    ],
                    flow: [
                        { title: 'Build The Dependency Graph', tone: 'info', body: 'Work is broken into steps with real dependencies, not a flat list run top to bottom.' },
                        { title: 'Run What\'s Ready, Bounded', tone: 'jargon', body: 'Independent steps run concurrently, capped at a configured limit — not unbounded parallelism.' },
                        { title: 'Nest Safely', tone: 'warn', body: 'A step can invoke an entire child plan, inheriting the same governance restrictions as its parent, with a hard depth limit.' },
                        { title: 'Never Spin Forever', tone: 'tip', body: 'If the scheduler ever finds itself with nothing progressing, it stops and reports rather than looping indefinitely.' },
                    ],
                    narrative: [
                        'A real planning engine needs more than "does the work eventually run" — it needs genuine concurrency control, safe nesting, and a way to notice when it\'s stuck.',
                        'What\'s real: steps that don\'t depend on each other actually run at the same time, bounded by a <mark class="hl">real concurrency limit</mark> — not unbounded, and not accidentally serial either. A step can invoke an entire nested sub-plan, and that nested plan <mark class="hl">inherits the parent\'s exact governance identity</mark> — a tool the parent was denied stays denied inside every sub-plan, automatically, not something each nested level has to remember to re-check. Nesting itself has a <mark class="hl">hard depth limit</mark>, so a plan that tries to recurse too deep is refused with a clear error instead of climbing until something breaks.',
                        'The honest defensive detail: the scheduler has an explicit check for <mark class="hl">"nothing is actually progressing"</mark> — if it ever finds pending work but nothing ready and nothing running, it stops and logs a warning rather than spinning forever waiting for progress that will never come.',
                    ],
                    techTable: {
                        columns: ['Step Type', 'What It Does'],
                        rows: [
                            ['LLM Call', 'A single call to a model.'],
                            ['Tool Use', 'A single tool invocation.'],
                            ['Human Gate', 'Pauses for a person\'s decision.'],
                            ['Conditional Branch', 'Chooses which path the plan takes next.'],
                            ['Sub-Plan Invocation', 'Runs an entire nested plan as one step.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Independent steps genuinely run concurrently',
                            body: 'A real concurrency limit bounds how many steps run at once — not just a graph structured as if they could run in parallel.',
                        },
                        {
                            title: 'A sub-plan inherits its parent\'s governance identity automatically',
                            body: 'A tool denied to the parent is denied to every nested plan beneath it, without each level needing its own explicit check.',
                        },
                        {
                            title: 'Nesting has a hard depth limit',
                            body: 'A runaway or malicious plan that tries to nest sub-plans indefinitely is refused with a clear error, not left to recurse until something crashes.',
                        },
                        {
                            title: 'The scheduler detects its own stuck state',
                            body: 'An explicit "nothing is progressing" check prevents an infinite loop when a plan gets into a state where nothing can move forward.',
                        },
                        {
                            title: 'Five distinct step types, not one generic "do a thing" step',
                            body: 'Each kind of work — model call, tool call, human gate, branch, nested plan — is its own keyed executor.',
                        },
                    ],
                    whyItMatters:
                        'A plan that can only run one step at a time, that nests sub-plans without limit, or that silently hangs when it gets stuck isn\'t something you could trust with real, multi-step work. Bounded concurrency, governance that survives nesting, and a scheduler that notices its own dead ends are what separate a genuine planning engine from a script that happens to work on the happy path.',
                },
            },
            {
                id: 'orchestrator',
                name: 'Orchestrator',
                status: 'built',
                exec: 'The assembly line every request runs through: check it’s safe, pick the right skill, get or build the right agent, run it, record what happened.',
                eng: 'ExecuteAgentTurnCommand flows through ~14 ordered MediatR pipeline behaviors before ExecuteAgentTurnCommandHandler resolves skills and calls agent.RunAsync.',
                docLink: '../04-message-journey.html',
                deepDive: {
                    scenario: [
                        {
                            time: 'A message arrives',
                            text: 'Before any actual business logic runs, it passes through roughly fourteen separate, ordered checks — exception handling, identity resolution, content safety, prompt-injection screening, budget tracking, and more — each one wrapping the next.',
                        },
                        {
                            time: 'Only then',
                            text: 'The real work happens: figuring out which skills this turn needs, and either reusing the exact agent already built for this conversation, or building one for the first time.',
                        },
                        {
                            time: 'Turn 30, an hour later',
                            text: 'That same conversation continues. The agent isn\'t rebuilt from scratch every time — the same live instance is reused, saving all the overhead of reconstructing it turn after turn.',
                        },
                        {
                            time: 'The conversation ends',
                            text: 'The cached agent is explicitly released rather than left sitting around. And if a conversation is simply abandoned instead of cleanly ended, a background timer quietly cleans it up anyway after half an hour of inactivity.',
                        },
                    ],
                    flow: [
                        { title: 'Run The Pipeline, In Order', tone: 'info', body: 'Roughly fourteen separate behaviors, each wrapping the next, catch problems before real work ever starts.' },
                        { title: 'Resolve What This Turn Needs', tone: 'jargon', body: 'Which skills apply, and what the agent needs access to.' },
                        { title: 'Reuse Or Build The Agent', tone: 'tip', body: 'The same live agent instance is reused across a conversation\'s turns rather than rebuilt from scratch every time.' },
                        { title: 'Clean Up When It\'s Done', tone: 'warn', body: 'The cached agent is explicitly released when a conversation ends, with a time-based safety net for ones that are simply abandoned.' },
                    ],
                    narrative: [
                        'The orchestrator is the actual assembly line every ordinary request runs through, from the moment it arrives to the moment something is recorded about what happened.',
                        'What\'s real: this is genuinely <mark class="hl">around fourteen separate, independently-authored checks and side effects</mark> layered in a specific order before the actual conversation logic ever runs — safety and identity checks wrap everything, while things like budget tracking and audit sit closer to the work itself. <mark class="hl">The order is deliberate</mark>, the same discipline seen in the tool-call governance layer elsewhere on this page.',
                        'One detail worth calling out: agents aren\'t rebuilt on every single turn. The same live agent is <mark class="hl">created once and reused for the rest of that conversation</mark> — explicitly released when the conversation ends, with a time-based safety net that quietly cleans up anything abandoned rather than cleanly closed.',
                    ],
                    techTable: {
                        columns: ['Behavior (a sample of ~14)', 'What It Does'],
                        rows: [
                            ['Unhandled Exception', 'Catches anything that goes wrong deeper in the pipeline.'],
                            ['Agent Identity Resolution', 'Establishes which agent is actually running this turn.'],
                            ['Content Safety', 'Screens output for policy violations before anything is trusted.'],
                            ['Prompt Injection', 'Screens for injected instructions in the incoming content.'],
                            ['Token Budget', 'Enforces spending limits for this conversation.'],
                            ['Audit Trail', 'Records what happened for later review.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: '~14 separate pipeline behaviors, not one big handler',
                            body: 'Each one independently authored, doing one job, wrapped around the next in a specific, deliberate order.',
                        },
                        {
                            title: 'Agents are cached per conversation, not rebuilt every turn',
                            body: 'A real efficiency detail — a conversation\'s 30th turn is exactly as cheap to set up as its first.',
                        },
                        {
                            title: 'Cache cleanup has two paths, not one',
                            body: 'Explicit eviction on a clean conversation end, plus a 30-minute sliding-TTL safety net for conversations that just get abandoned.',
                        },
                        {
                            title: 'Multiple skills merge into one agent context',
                            body: 'A turn needing more than one skill doesn\'t spin up multiple agents — they merge into a single execution context.',
                        },
                        {
                            title: 'The behavior order is deliberate, not incidental',
                            body: 'Since each one wraps the next, what runs first can see and stop things that would otherwise reach deeper layers.',
                        },
                    ],
                    whyItMatters:
                        '"The orchestrator" sounds like it should be one big function doing everything, but a system this safe and reliable at scale is actually built from many small, independently-testable pieces stacked in a specific order — not one place where every concern is tangled together. That\'s what makes it possible to add a new safety check or a new kind of tracking without having to understand or risk breaking everything else already in the pipeline.',
                },
            },
        ],
    },
    {
        id: 'agent-runtime',
        number: '04',
        name: 'Agent Runtime',
        subtitle: 'The agents themselves',
        boxes: [
            {
                id: 'supervisor-agent',
                name: 'Supervisor Agent',
                status: 'partial',
                exec: 'A real manager-plus-specialists multi-agent pattern exists — with human plan review and full telemetry — but it\'s not yet reachable from an ordinary conversation. Today it\'s a demonstrated, tested building block, not something a live chat turn can invoke.',
                eng: 'MagenticOrchestrator wraps the Microsoft Agent Framework\'s own (experimental) Magentic multi-agent workflow (MagenticWorkflowBuilder(manager).AddParticipants(...)), adding OTel spans, a human-in-the-loop plan-review bridge, and content-safety sanitization. Demonstrated in Presentation.ConsoleUI\'s MagenticOrchestrationExample; no CQRS command yet exposes it on a live conversation turn.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A task too big for one agent',
                            text: 'Something genuinely needs a manager that breaks the work down and delegates pieces to a team of specialist agents, rather than one agent trying to do everything itself.',
                        },
                        {
                            time: 'That pattern is invoked',
                            text: 'A manager agent is created alongside a set of specialist participants, and the whole workflow runs as a real multi-agent collaboration — not a single agent juggling several roles internally.',
                        },
                        {
                            time: 'The manager gets stuck',
                            text: 'Partway through, the manager\'s plan stalls or needs a second opinion. Instead of guessing, the whole workflow pauses and a real person is asked to review the plan — the exact same escalation mechanism used everywhere else in this harness.',
                        },
                        {
                            time: 'Today, though',
                            text: 'None of this is reachable from an ordinary chat conversation. It exists, works, and is demonstrated end-to-end — but only as a capability someone would have to deliberately invoke themselves, not something the everyday product surface offers yet.',
                        },
                    ],
                    flow: [
                        { title: 'Build The Team', tone: 'info', body: 'A manager agent is paired with a set of specialist participant agents for one workflow.' },
                        { title: 'Run, Emit Real Telemetry', tone: 'jargon', body: 'The whole multi-agent run is traced with the same OpenTelemetry span tree as any other operation in this harness.' },
                        { title: 'Pause For A Person When Needed', tone: 'warn', body: 'A stalled or uncertain plan pauses the workflow and routes to the same human-escalation mechanism used everywhere else.' },
                        { title: 'Not Yet On The Live Path', tone: 'tip', body: 'A real command or endpoint that lets an ordinary conversation trigger this doesn\'t exist yet — it\'s demonstrated, not exposed.' },
                    ],
                    narrative: [
                        'This is a genuine correction to what this box used to say: a real manager-and-specialists multi-agent pattern <mark class="hl">already exists in this codebase</mark> — it does not need to be built from scratch.',
                        'What\'s real: this harness wraps a genuine multi-agent workflow — a manager agent plus a team of specialist participants — with production-grade care around it: <mark class="hl">every run gets the same OpenTelemetry span tree</mark> as any other operation, and a plan that stalls or needs review <mark class="hl">pauses for a real human decision</mark> through the exact same escalation mechanism the rest of this harness uses, including the same unforgeable-feedback wrapping seen in Human Review.',
                        'Why it stays <mark class="hl">"partial"</mark>: the underlying multi-agent engine itself is an experimental feature of the Microsoft Agent Framework this harness builds on, and — more importantly — nothing in the live conversation path invokes it yet. It\'s demonstrated in a runnable example and fully tested, but there\'s no command or endpoint today that lets an ordinary chat turn actually kick off a multi-agent run.',
                    ],
                    techTable: {
                        columns: ['What This Harness Adds', 'Why It Matters'],
                        rows: [
                            ['OpenTelemetry span emission', 'A multi-agent run is traceable the same way any other operation in this harness is — not a black box.'],
                            ['Human-in-the-loop plan review', 'A stalled or uncertain plan pauses for a real person instead of the manager guessing its way forward.'],
                            ['Content-safety sanitization', 'Human revision feedback relayed back to the manager model is sanitized and wrapped, the same as any other human-to-model relay.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'A real manager-plus-participants API, not a placeholder',
                            body: 'The workflow is built with an explicit manager agent and a set of participant agents — a literal multi-agent composition, not one agent role-playing several personas.',
                        },
                        {
                            title: 'Reuses the harness\'s own escalation mechanism',
                            body: 'A plan-review pause is translated into the same escalation request type used elsewhere, rather than inventing a second, parallel approval system.',
                        },
                        {
                            title: 'Human feedback to the manager is wrapped the same way as everywhere else',
                            body: 'Revision feedback relayed back to the manager model goes through the identical sanitize-then-wrap mechanism verified under Human Review — one of only two places in this harness where a human\'s own words are deliberately handed to an LLM.',
                        },
                        {
                            title: 'The underlying engine is explicitly marked experimental',
                            body: 'This harness pins to the framework\'s public-only surface and suppresses one specific experimental-API warning rather than reaching into internal types — a deliberate, narrow dependency, not an accidental one.',
                        },
                        {
                            title: 'Demonstrated, not yet wired to a live entry point',
                            body: 'A runnable console example exercises the full pattern end-to-end; no controller or CQRS command in the running hosts invokes it today.',
                        },
                    ],
                    whyItMatters:
                        'Some problems genuinely need more than one agent working together — a manager that plans and a team that executes, with a person able to step in when the plan gets stuck. Building that safely, with real observability and real human oversight, is most of the hard work. What\'s left is the more mundane task of actually connecting it to something a user can trigger — which is exactly why this is marked "partial" rather than either "built" or "not built."',
                },
            },
            {
                id: 'specialist-agents',
                name: 'Specialist Agents',
                status: 'partial',
                exec: 'The same real manager-plus-specialists pattern as Supervisor Agent — specialist participant agents are a genuine part of the Magentic workflow this harness wraps, not a placeholder — but, same caveat, nothing in the live conversation path spins one up today.',
                eng: 'Specialist agents are the MagenticWorkflowBuilder\'s AddParticipants(...) — independent agents with their own instructions/tools, coordinated by the manager, not skills merged into one agent\'s context. Demonstrated, not yet exposed on a live path.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A job needs different expertise',
                            text: 'Part of a task needs one kind of specialized handling, another part needs something completely different. Rather than one agent trying to be good at both, two genuinely separate specialist agents each own their own piece.',
                        },
                        {
                            time: 'The manager delegates',
                            text: 'The manager agent doesn\'t do the specialized work itself — it hands pieces of the task to whichever specialist participant is suited for that piece, and coordinates the results.',
                        },
                        {
                            time: 'Compare that to skills',
                            text: 'This is a genuinely different shape from a single agent loading multiple skills into one shared context — here, each specialist is its own independent agent instance, not an instruction document merged into someone else\'s.',
                        },
                        {
                            time: 'Today',
                            text: 'That distinction is real in the code, tested, and demonstrated — but nothing in this harness\'s live conversation flow actually assembles a team of specialists for an ordinary user request yet.',
                        },
                    ],
                    flow: [
                        { title: 'Each Specialist Is Its Own Agent', tone: 'info', body: 'Not a skill merged into a shared context — an independent agent instance with its own instructions and tools.' },
                        { title: 'The Manager Coordinates, Doesn\'t Do The Work', tone: 'jargon', body: 'Delegation and coordination are the manager\'s job; the specialists do the actual specialized work.' },
                        { title: 'Traced And Escalatable Like Any Other Run', tone: 'tip', body: 'The same telemetry and human-review mechanisms apply to a team of specialists as to any other harness operation.' },
                        { title: 'Not Yet Assembled On A Live Path', tone: 'warn', body: 'Nothing in the running hosts today builds a specialist team for an ordinary conversation.' },
                    ],
                    narrative: [
                        'The distinction this box draws matters: a skill merged into one agent\'s context is <mark class="hl">still one agent</mark> wearing different instructions. A specialist participant in this harness\'s multi-agent workflow is <mark class="hl">a genuinely separate agent instance</mark> that the manager delegates to and coordinates — a real architectural difference, not a semantic one.',
                        'What\'s real: that separation exists in the code today, wired into the same production-grade wrapper as Supervisor Agent — full tracing, human plan review, safety sanitization on any human feedback relayed to the manager. It\'s not a sketch of the idea; it\'s a working, tested implementation.',
                        'Why it\'s still <mark class="hl">"partial"</mark>, identically to Supervisor Agent: nothing in the harness\'s actual conversation path assembles a team of specialists for a real user request today. The capability is genuine; the everyday product surface for triggering it doesn\'t exist yet.',
                    ],
                    techTable: {
                        columns: ['Skill (in one agent)', 'Specialist Agent (Magentic)'],
                        rows: [
                            ['Instructions merged into a shared execution context', 'A fully independent agent instance with its own context'],
                            ['Coordinated implicitly by one agent\'s own reasoning', 'Explicitly delegated to and coordinated by a separate manager agent'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'A genuinely separate agent instance, not a merged context',
                            body: 'Each specialist participant is its own agent, distinct from the multi-skill-in-one-agent pattern the harness also supports elsewhere.',
                        },
                        {
                            title: 'Delegation is explicit, not implicit',
                            body: 'The manager agent decides what work goes to which specialist — it\'s a real coordination structure, not one agent internally switching personas.',
                        },
                        {
                            title: 'Inherits the same observability and safety wrapper',
                            body: 'Nothing about specialist agents skips the tracing, human-review, or content-safety layers this harness applies to the manager and the workflow as a whole.',
                        },
                        {
                            title: 'No live assembly point exists yet',
                            body: 'Building a specific team of specialists for a real task is something a caller has to do explicitly today — there\'s no product surface that does it automatically for an ordinary request.',
                        },
                    ],
                    whyItMatters:
                        'Some tasks are genuinely better solved by a team of specialists than by one generalist agent trying to do everything — the same reason organizations have specialized roles instead of one person doing every job. This harness has already built the hard, safety-conscious part of that pattern; what\'s missing is simply a front door that lets an ordinary request reach it.',
                },
            },
            {
                id: 'skills-system',
                name: 'Skills System',
                status: 'built',
                exec: 'Short instruction documents that tell an agent what role to play and what it’s allowed to do, loaded only as needed so adding more skills never slows things down.',
                eng: 'SKILL.md files loaded via three-tier progressive disclosure; SkillMetadataParser → SkillDefinition; multi-skill agents merge several via AgentExecutionContextFactory with prerequisite ordering.',
                docLink: '../05-skills.html',
                tech: 'SKILL.md · SkillDefinition',
                deepDive: {
                    scenario: [
                        {
                            time: 'Dozens of skills exist',
                            text: 'A single conversation turn only needs one or two of them. Loading every skill\'s full instructions for every turn would burn a large chunk of the model\'s attention on things that don\'t apply right now.',
                        },
                        {
                            time: 'Instead',
                            text: 'Only a lightweight index card — name and description — is kept loaded for every available skill. The full instructions only get pulled in once a skill is actually selected for this turn.',
                        },
                        {
                            time: 'A skill needs more still',
                            text: 'A script, a template, a reference document. That loads only at the exact moment the skill actually runs and needs it — not a moment before.',
                        },
                        {
                            time: 'A surprising wrinkle',
                            text: 'Someone hand-writes a skill file with a structured field describing exactly which tools it needs. The open-source framework this harness builds on would have silently dropped that field entirely — no error, no warning — because its own reader can only capture flat text, not a structured list.',
                        },
                    ],
                    flow: [
                        { title: 'Load The Index Card First', tone: 'info', body: 'Name and description only — cheap enough to keep every skill\'s index loaded at all times.' },
                        { title: 'Load Full Instructions On Selection', tone: 'jargon', body: 'The complete instruction document only loads once a skill is actually chosen for this turn.' },
                        { title: 'Load Supporting Files On Execution', tone: 'tip', body: 'Scripts, templates, and references load only at the moment a skill actually runs and needs them.' },
                        { title: 'Catch What The Framework Would Drop', tone: 'warn', body: 'This harness\'s own parser captures structured fields the underlying framework\'s own reader silently discards.' },
                    ],
                    narrative: [
                        'A skill is a short instruction document telling an agent what role to play and what it\'s allowed to do — loaded in layers, cheapest first, so having many skills available never taxes the model\'s attention budget on the ones not actually being used.',
                        'What\'s real, and a genuinely surprising detail: the open-source agent framework this harness is built on has its own way of reading a skill file, and it <mark class="hl">silently discards any custom field it doesn\'t already know about</mark>, with no warning at all. It also <mark class="hl">can\'t represent some of what this harness needs to describe</mark> — a structured list of tool declarations, a nested set of network rules — its own fallback only holds flat text. This harness runs its own additional parser specifically to catch and correctly represent everything the framework\'s reader would otherwise quietly lose.',
                        'A second real distinction worth understanding: a skill written specifically for this harness <mark class="hl">declares its exact tools one by one</mark>; a skill bundled with an external plugin instead <mark class="hl">gets access to everything that plugin\'s own tool server offers, wholesale</mark> — a deliberately different, broader trust model for plugin-sourced skills versus the harness\'s own.',
                    ],
                    techTable: {
                        columns: ['Tier', 'What Loads', 'When'],
                        rows: [
                            ['Index card', 'Name and description only.', 'At startup, for every available skill.'],
                            ['Full instructions', 'The complete instruction document.', 'Once a skill is actually selected.'],
                            ['Supporting files', 'Scripts, templates, references.', 'Only when the skill actually executes and needs them.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The framework\'s own parser silently drops unknown fields',
                            body: 'No error, no warning — a real, documented gap this harness\'s own parser exists specifically to work around.',
                        },
                        {
                            title: 'The framework can\'t represent structured data at all',
                            body: 'Its flat-string-only fallback can\'t hold a list of tool declarations or a nested set of network rules, so this harness maintains its own parser to make those fields reach the runtime.',
                        },
                        {
                            title: 'Two distinct trust models for skill-declared tools',
                            body: 'A harness-native skill declares its tools precisely, one by one; a plugin-sourced skill gets everything its plugin\'s tool server offers, wholesale.',
                        },
                        {
                            title: 'Skill parsing includes a real security scan',
                            body: 'A dedicated scanner is part of the parsing pipeline itself, not bolted on separately afterward.',
                        },
                        {
                            title: 'Prerequisites are enforced, not just documented',
                            body: 'A multi-skill agent\'s skills load in an order that respects declared dependencies between them, not whatever order they happen to be listed in.',
                        },
                    ],
                    whyItMatters:
                        'An agent that can draw on many different skills is only actually cheap and fast if "many skills available" doesn\'t mean paying the token cost of all of them on every single turn. And building on an open-source framework doesn\'t mean trusting it blindly — this harness had to notice, and specifically guard against, an entire class of silent data loss the framework\'s own parser would otherwise cause.',
                },
            },
            {
                id: 'context-budget',
                name: 'Context Budget',
                status: 'built',
                exec: 'Tracks how much of the model’s attention and cost budget a conversation has used, and stops things before they run away.',
                eng: 'IConversationBudgetTracker (InProcessConversationBudgetTracker) plus TokenEstimationHelper enforce a cross-turn token ceiling.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A single turn',
                            text: 'Is capped so it can\'t blow through a hard ceiling by itself — if it would, that\'s caught and stopped before the call even goes out.',
                        },
                        {
                            time: 'Separately, and more subtly',
                            text: 'The whole conversation — every turn added together — has its own running total. Once that crosses a threshold, the conversation is flagged so the loop can stop gracefully on the next turn, rather than being cut off mid-response.',
                        },
                        {
                            time: 'Something different entirely',
                            text: 'A background multi-step plan has its own budget too, tracked under its own separately namespaced key — specifically so a plan\'s spending can never accidentally erase or get mixed up with a conversation\'s total, even if they happen to share an identifying string.',
                        },
                        {
                            time: 'A named risk',
                            text: 'In a deployment where two different services can both continue the same conversation, each enforcing its own private copy of the budget would let that conversation spend roughly double what it should — a real risk this component\'s own documentation calls out directly.',
                        },
                    ],
                    flow: [
                        { title: 'Cap Each Turn On Its Own', tone: 'info', body: 'A hard per-turn ceiling throws before an over-budget call goes out.' },
                        { title: 'Track The Whole Conversation Separately', tone: 'jargon', body: 'A running total across every turn is checked between turns, not mid-turn, so it can end things gracefully.' },
                        { title: 'Keep Budgets From Colliding', tone: 'tip', body: 'A plan run and a conversation get separately namespaced keys, so one can never accidentally erase or share the other\'s total.' },
                        { title: 'Watch For The Split-Brain Risk', tone: 'warn', body: 'A shared ceiling enforced independently by two different processes can silently double the real limit unless both read one shared, current total.' },
                    ],
                    narrative: [
                        'The point is knowing how much of the model\'s attention and cost a conversation has used, and stopping gracefully before it runs away — not ignoring cost entirely, and not crashing mid-answer either.',
                        'What\'s real: two genuinely different budgets exist for two genuinely different problems. One caps a single turn and <mark class="hl">throws immediately</mark> if it would be exceeded; the other tracks the whole conversation\'s running total and is checked <mark class="hl">between turns, specifically so it can end things gracefully</mark> instead of abruptly. The system is also deliberately careful that a background plan\'s own budget can never collide with a conversation\'s, even sharing an identifying string, by treating every key as opaque and namespacing it.',
                        'One honest, self-documented caveat: the interface itself warns that if <mark class="hl">two separate host processes each enforce their own private copy of one ceiling, a conversation can spend roughly double what it\'s supposed to</mark> — and the implementation this harness ships by default is exactly the kind that warning describes. A deployment that actually runs multiple host processes against the same conversations needs a shared, durable implementation in its place, not the default.',
                    ],
                    techTable: {
                        columns: ['Tracker', 'Scope', 'Behavior On Overage'],
                        rows: [
                            ['Per-turn budget', 'One single turn.', 'Throws immediately, before the call goes out.'],
                            ['Conversation budget', 'Every turn in the conversation, added together.', 'Reports exhausted; the caller stops gracefully on the next turn — never throws.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Two genuinely separate budget concepts',
                            body: 'A per-turn ceiling that throws pre-flight, and a whole-conversation running total checked between turns that never throws.',
                        },
                        {
                            title: 'Budget keys are treated as opaque, on purpose',
                            body: 'A plan run gets its own namespaced key precisely so its spending can never collide with a conversation\'s total that happens to share an identifier.',
                        },
                        {
                            title: 'The interface documents a real multi-process risk',
                            body: 'Two hosts each enforcing a private copy of the same ceiling can let a conversation spend roughly double the real limit.',
                        },
                        {
                            title: 'The default implementation is exactly the kind that risk describes',
                            body: 'InProcessConversationBudgetTracker — a deployment spanning multiple host processes needs a shared, durable implementation to actually enforce one true ceiling.',
                        },
                        {
                            title: 'Entries are reclaimed, not kept forever',
                            body: 'A long-lived deployment accumulates many tracked keys; implementations are expected to evict or expire them rather than grow unbounded.',
                        },
                    ],
                    whyItMatters:
                        'An agent that can run for many turns, or spin off background plans, needs a real answer to "how much has this actually cost so far" that survives more than one request — not a number that resets by accident or silently doubles because two servers each think they\'re the only one watching. Being explicit about which budget is being enforced, and honest about where the current default implementation\'s real limit is, is what keeps a cost control from becoming a false sense of one.',
                },
            },
            {
                id: 'meta-harness',
                name: 'Meta-Harness',
                status: 'built',
                exec: 'The system can review its own past failures, propose changes to a skill’s instructions, and test that proposal against a benchmark before adopting it.',
                eng: 'RunHarnessOptimizationCommand: snapshot → propose → evaluate against a benchmark task suite → regression-gate against prior winners → promote.',
                docLink: '../14-skill-training.html',
                deepDive: {
                    scenario: [
                        {
                            time: 'A weak spot',
                            text: 'A skill has been quietly underperforming on a specific kind of question. The system reviews its own past benchmark runs and proposes a specific rewrite meant to fix it — then actually tests the proposal against a real benchmark, rather than trusting its own claim that the rewrite is better.',
                        },
                        {
                            time: 'A hidden cost',
                            text: 'The rewrite genuinely improves the target problem — but, unnoticed, it also quietly breaks a completely different case an earlier version had already solved. The candidate isn\'t accepted just because its overall average looks better; a separate check specifically catches that regression and rejects the change.',
                        },
                        {
                            time: 'A permanent memory',
                            text: 'Because that earlier case was solved once before by a past accepted version, it\'s permanently added to a growing list of things that must never break again — so every future proposal, not just this one, gets checked against it forever.',
                        },
                        {
                            time: 'A bad attempt',
                            text: 'One proposal in a batch throws an unexpected error partway through. That single failure doesn\'t sink the whole optimization run — it\'s recorded as a failed attempt, and the loop moves on to the next idea.',
                        },
                    ],
                    flow: [
                        { title: 'Review Past Performance', tone: 'info', body: 'Real evaluation history, not a guess, identifies where the current version falls short.' },
                        { title: 'Propose A Specific Change', tone: 'jargon', body: 'A concrete rewrite of the skill\'s instructions, not a vague suggestion.' },
                        { title: 'Test It For Real', tone: 'tip', body: 'The proposal runs against an actual benchmark suite before anyone decides anything about it.' },
                        { title: 'Check It Never Un-Solves Something', tone: 'warn', body: 'A separately-maintained, ever-growing list of previously-fixed cases has to keep passing, or the "improvement" is rejected regardless of its overall score.' },
                    ],
                    narrative: [
                        'This is the system reviewing its own history, proposing a concrete change, and testing that change against real evidence before adopting it — not just trusting an AI\'s own claim that its rewrite is better.',
                        'What\'s real, and the most elegant part: acceptance isn\'t based on a single aggregate score. A separate, <mark class="hl">self-maintained regression suite grows automatically</mark> every time a new version is accepted — any specific case that version newly fixed gets permanently added to a "must never break again" list. A later proposal that improves the overall average but secretly regresses one of those previously-solved cases <mark class="hl">gets rejected, even though its headline number looks better</mark>. The check is conservative on purpose too: a tracked case that\'s simply <mark class="hl">missing from a new proposal\'s results counts as a failure</mark>, not a pass by default.',
                        'The resilience detail: one bad proposal in a batch — a parsing error, an evaluation exception — <mark class="hl">doesn\'t take down the whole optimization run</mark>. It\'s recorded as a failed attempt and the loop moves on to the next idea.',
                    ],
                    techTable: {
                        columns: ['Stage', 'What Happens'],
                        rows: [
                            ['Snapshot', 'Capture the current version of the skill as the baseline to improve on.'],
                            ['Propose', 'Generate a specific, concrete rewrite — not a vague direction.'],
                            ['Evaluate', 'Run the proposal against a real benchmark task suite.'],
                            ['Regression-gate', 'Check it against every previously-solved case that must keep passing.'],
                            ['Promote', 'Accept it as the new best, and add anything it newly fixed to the permanent list.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The regression suite is self-maintaining',
                            body: 'It grows automatically every time a new best candidate is accepted, seeded from whatever cases that candidate fixed — no one hand-curates it.',
                        },
                        {
                            title: 'A missing result counts as a failure, not a pass',
                            body: 'A previously-solved case absent from a new proposal\'s results counts against it — the conservative reading, not the generous one.',
                        },
                        {
                            title: 'Improvement alone doesn\'t win',
                            body: 'A candidate with a better overall score can still be rejected if it regresses even one case already in the growing regression suite.',
                        },
                        {
                            title: 'One failed proposal doesn\'t end the run',
                            body: 'Parsing errors and evaluation exceptions are caught per-attempt, recorded, and the loop continues to the next idea.',
                        },
                        {
                            title: 'The first accepted candidate seeds the whole suite',
                            body: 'Everything it gets right becomes the baseline every future proposal has to keep matching.',
                        },
                    ],
                    whyItMatters:
                        'A system that improves itself only on the score it\'s being measured by will happily trade an old, quietly-working case for a new, showier one — the classic failure mode of "improved the metric, broke something real." Making the improvement process check itself against everything it has already gotten right before, not just the thing it\'s currently trying to fix, is what actually earns the word "improvement" instead of just "different."',
                },
            },
        ],
    },
    {
        id: 'model-gateway',
        number: '05',
        name: 'Model Gateway',
        subtitle: 'Talks to the underlying LLMs',
        boxes: [
            {
                id: 'multi-model-routing',
                name: 'Multi-Model Routing',
                status: 'built',
                exec: 'Genuinely dynamic, cross-provider, per-request routing — on by default, not a thin wrapper around a single fixed provider.',
                eng: 'ModelRouter picks a cost-ordered ModelTier per request (each tier can point to a completely different provider), with per-conversation escalation on repeated bad outcomes; tiers separately reference a named fallback chain for provider outages.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Two providers, one decision',
                            text: 'A cheap, fast provider is configured for easy questions; a more capable, more expensive one for hard reasoning. The choice between them isn\'t hardcoded — it\'s made per request, based on how hard that specific request actually is.',
                        },
                        {
                            time: 'A struggling conversation',
                            text: 'A conversation starts on the cheap tier. Several turns in, it starts showing signs of trouble. Rather than staying stuck, that one conversation is automatically escalated to a more capable tier — unrelated conversations stay wherever their own complexity puts them.',
                        },
                        {
                            time: 'A provider outage',
                            text: 'Separately, each tier can point to its own named fallback chain — so if the specific provider a tier uses has an outage, that\'s handled by an entirely separate, already-solved mechanism, not something tier selection needs to know about.',
                        },
                        {
                            time: 'If it\'s all turned off',
                            text: 'The whole system falls back to one fixed default tier. Nothing breaks — it just stops being adaptive.',
                        },
                    ],
                    flow: [
                        { title: 'Classify Then Choose', tone: 'info', body: 'Complexity drives which cost-ordered tier a request starts on.' },
                        { title: 'Different Tiers, Different Providers', tone: 'jargon', body: 'A tier isn\'t just "cheaper GPT" — it can point to an entirely different AI provider altogether.' },
                        { title: 'Escalate The Struggling Conversation, Not Everyone', tone: 'warn', body: 'A per-conversation track record can bump just that conversation to a higher tier.' },
                        { title: 'Let Fallback Chains Handle Outages', tone: 'tip', body: 'If the chosen tier\'s provider is actually down, that\'s a separate, already-solved concern.' },
                    ],
                    narrative: [
                        'For "multi-model routing" to be real, it needs to mean more than "we can talk to more than one provider" — it needs a genuine, per-request decision about which provider handles a given job, made dynamically rather than fixed at deployment time.',
                        'Correcting an earlier, too-modest description of this box: tier selection is <mark class="hl">on by default, genuinely cross-provider</mark> — each tier can be an entirely different AI provider, not just a cheaper deployment of the same one — and <mark class="hl">adapts per conversation</mark> when things aren\'t going well, not a static, one-size-fits-all mapping.',
                        'How it stays clean: tier selection (which model handles a normal request) and fallback chains (what happens when a chosen provider is actually failing) are deliberately kept as <mark class="hl">two separate, cooperating mechanisms</mark> rather than one tangled system — a tier just names a fallback chain, and each side solves its own problem.',
                    ],
                    techTable: {
                        columns: ['Concern', 'Handled By'],
                        rows: [
                            ['Which tier handles a request, by complexity', 'The router\'s complexity classification and cost-ordered tier list.'],
                            ['What happens if that tier\'s provider goes down', 'A separately-configured, named fallback chain.'],
                            ['A specific conversation that\'s struggling', 'Per-conversation escalation, independent of every other conversation.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Tiers are genuinely cross-provider, not cross-deployment',
                            body: 'A tier\'s client type can point to a completely different AI provider than another tier — not just a cheaper deployment of the same one.',
                        },
                        {
                            title: 'Dynamic routing is on by default',
                            body: 'A single config flag disables it, falling back to one static tier — this isn\'t an opt-in extra.',
                        },
                        {
                            title: 'Escalation is scoped to the one struggling conversation',
                            body: 'Other conversations are unaffected by one conversation\'s bad run.',
                        },
                        {
                            title: 'Tier selection and failure recovery are deliberately decoupled',
                            body: 'A tier names a fallback chain rather than tier logic reimplementing retry or circuit-breaking itself.',
                        },
                        {
                            title: 'Tiers are cost-ordered automatically',
                            body: 'Config doesn\'t manually rank tiers — they\'re sorted by their own declared cost per 1K tokens.',
                        },
                    ],
                    whyItMatters:
                        'Real deployments care about both "don\'t overpay for easy questions" and "don\'t underpower hard ones," and doing that well means genuinely choosing between different providers per request — not just having more than one configured somewhere. Keeping that proactive choice cleanly separate from what happens when a provider is actually down is what keeps this system understandable instead of one tangled mass of routing-and-retry logic.',
                },
            },
            {
                id: 'fallback-chains',
                name: 'Fallback Chains',
                status: 'built',
                exec: 'If one AI provider fails, the harness automatically tries the next one in line — real resilience, not just a retry loop.',
                eng: 'IProviderErrorClassifier normalizes failures across providers (each SDK throws a different exception type for the same HTTP status); Polly owns retry and circuit-breaking, chain members run with SDK retry disabled.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Rate-limited',
                            text: 'A request to one provider gets rate-limited — a classic "try again in a moment" situation. It\'s retried automatically, and if it keeps failing, it\'s counted against that provider\'s health and the harness moves on to the next one in the chain.',
                        },
                        {
                            time: 'A missing deployment',
                            text: 'A different request fails because the specific model doesn\'t exist on this particular provider. Retrying the same provider is pointless — but a different one in the chain might actually serve it, so that\'s tried instead, without wasting time retrying uselessly first.',
                        },
                        {
                            time: 'A rejected API key',
                            text: 'A failure turns out to be an account-level problem, not a provider-level one. Rotating through every other provider wouldn\'t fix this — it would waste time and hide the real problem behind what looks like a generic outage. The harness stops immediately with a clear, specific error instead.',
                        },
                        {
                            time: 'A user cancels',
                            text: 'Someone simply cancels their own request mid-flight. That\'s not a sign anything is wrong with any provider — so it\'s not counted against anyone\'s health, and the harness doesn\'t waste time trying another provider for a request nobody is waiting on anymore.',
                        },
                    ],
                    flow: [
                        { title: 'Classify By What Can Be Done', tone: 'info', body: 'Not by which .NET exception type happened to be thrown.' },
                        { title: 'Retry Only What\'s Worth Retrying', tone: 'jargon', body: 'A failure that might succeed the second time gets retried; one that provably won\'t doesn\'t.' },
                        { title: 'Fall Back Only When It Could Help', tone: 'tip', body: 'A provider-specific problem tries the next provider; a shared, account-level problem doesn\'t bother.' },
                        { title: 'Never Confuse "The User Left" With "The Provider Failed"', tone: 'warn', body: 'A caller\'s own cancellation is recognized as exactly that, not treated as a provider health signal.' },
                    ],
                    narrative: [
                        'Real fallback needs more than "try another provider" — it needs to know which kind of failure justifies retrying, which kind justifies trying a different provider, and which kind means neither will help.',
                        'What\'s real, and the specific bug this closes: each provider\'s own SDK throws a <mark class="hl">completely different .NET exception type for the identical HTTP status</mark> — so resilience logic built around matching exception types only ever really works for whichever provider happens to match, silently leaving the others unhandled. This harness instead classifies every failure into <mark class="hl">one of five distinct outcomes</mark>: retried and falls back, doesn\'t retry but still falls back, doesn\'t retry and specifically <mark class="hl">refuses to fall back</mark> because the problem is shared across every provider, an honestly-labeled "don\'t know" that still counts against the provider defensively, and a caller\'s own cancellation, recognized as not being a provider problem at all.',
                        'The specific care taken with cancellation: distinguishing "the caller actually gave up" from "something else threw a cancellation-shaped exception" uses the <mark class="hl">original token the caller passed in</mark>, not a derived one a retry layer might have created along the way — getting this wrong either wastes effort retrying an abandoned request, or worse, blames a provider for a cancellation that was never about the provider at all.',
                    ],
                    techTable: {
                        columns: ['Failure Kind', 'Retried?', 'Falls Back To Another Provider?'],
                        rows: [
                            ['Transient (rate limit, timeout)', 'Yes', 'Yes, if retries are exhausted.'],
                            ['Fatal for this provider (missing deployment)', 'No', 'Yes — a different provider may serve it.'],
                            ['Fatal for the whole chain (bad API key)', 'No', 'No — rotating providers would hide the real cause.'],
                            ['Unrecognized', 'No', 'Yes, but flagged as unresolved.'],
                            ['Caller cancelled', 'No', 'No — nobody is waiting for a response anymore.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'One shared classifier, not per-provider exception matching',
                            body: 'Exactly the bug this replaces: type-based handling only ever really worked for whichever provider\'s SDK happened to match.',
                        },
                        {
                            title: 'Five distinct outcomes, not a binary retry/don\'t-retry',
                            body: 'Each with independently justified retry, circuit-breaker-counting, and fallback behavior.',
                        },
                        {
                            title: 'A shared-cause failure deliberately refuses to fall back',
                            body: 'Rotating through every provider for a bad API key would waste time and disguise the real problem as a generic outage.',
                        },
                        {
                            title: 'An unrecognized failure still counts against the provider',
                            body: 'A defensive default that treats "don\'t know" as evidence, not as a free pass.',
                        },
                        {
                            title: 'Cancellation detection uses the original, ambient token',
                            body: 'Not a derived one a resilience layer created — so a caller\'s real withdrawal is never confused with an unrelated cancellation-shaped failure.',
                        },
                    ],
                    whyItMatters:
                        'A fallback system that just retries everything the same way, or rotates providers no matter what actually went wrong, either wastes time on failures that were never going to succeed or hides real, actionable problems behind a vague "service unavailable." Classifying failures by what can actually be done about them — not by which exception type a particular SDK happened to throw — is what makes multi-provider resilience genuinely useful instead of a false sense of it.',
                },
            },
            {
                id: 'semantic-cache',
                name: 'Semantic Cache',
                status: 'not-built',
                exec: 'Not built. There’s no cache that recognizes "this is basically the same question as before" and reuses an answer.',
            },
            {
                id: 'pii-redaction',
                name: 'PII Redaction',
                status: 'built',
                exec: 'Personal and sensitive data gets scrubbed out of logs and telemetry before it’s ever written down.',
                eng: 'RedactionCategory + RedactionRule drive redaction in ContentCaptureConfig/LogsConfig, governed by PiiFilteringConfig.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A tool call fails',
                            text: 'Its error message happens to include something that looks like a Slack access token, buried in the raw response text. That error is about to be written into the audit trail, an escalation record, and a live monitoring dashboard.',
                        },
                        {
                            time: 'Before any of that happens',
                            text: 'The harness recognizes the specific shape of a vendor API token — not just "looks like a random string" — and strips it out.',
                        },
                        {
                            time: 'Elsewhere, a different case',
                            text: 'A span sent to a monitoring dashboard includes a user\'s email address. Rather than deleting it and losing the ability to answer "how many distinct users hit this error," it\'s replaced with a one-way hash — the same email always produces the same hash, so analytics still work, but nobody looking at the dashboard can recover the real address.',
                        },
                        {
                            time: 'A category gets added later',
                            text: 'Because of how the "redact everything, no exceptions" call sites are written, they all pick up the new category automatically — none of them keeps its own separate, hand-copied list that could quietly miss the update.',
                        },
                    ],
                    flow: [
                        { title: 'Recognize The Shape', tone: 'info', body: 'Not just "sensitive-looking text" — specific, named patterns: emails, tokens, card numbers, particular vendor API key formats.' },
                        { title: 'Choose Delete Or Hash', tone: 'jargon', body: 'Some values have zero use even anonymized and get removed outright; others get a consistent hash that keeps analytical value without exposing the real value.' },
                        { title: 'Cover Every Place It Could Leak', tone: 'warn', body: 'Logs, traces, the audit trail, escalation records, and error text streamed to a live UI all go through the same redaction.' },
                        { title: 'Never Rely On A Hand-Copied List', tone: 'tip', body: 'A single, computed "every category" source feeds every unconditional-redaction call site.' },
                    ],
                    narrative: [
                        'Real PII redaction means recognizing specific, named patterns — not guessing at anything that looks sensitive — and applying that consistently everywhere sensitive content could actually leak, not just in the one place someone remembered to add a check.',
                        'What\'s real: <mark class="hl">nine distinct, named categories</mark> are recognized — not just email and phone, but specific vendor API key shapes (OpenAI, GitHub, and Slack tokens each have their own recognizable prefix and structure). Two genuinely different actions are available depending on the value: some things are <mark class="hl">deleted outright</mark> because they have no use even anonymized, others are replaced with a <mark class="hl">consistent one-way hash</mark> that keeps analytical value — the same input always hashes the same way — without exposing the real content.',
                        'The real fix worth calling out: this used to be <mark class="hl">four separate, hand-copied "every category" lists</mark> scattered across the log, trace, and tool-error redaction paths, each with its own near-identical justification comment. A category missed in even one of those four lists would leak through that one path. Now there\'s exactly <mark class="hl">one computed source</mark>, and every one of those call sites reads from it — a new category added to the enum reaches all of them automatically, closing off the exact class of bug that happened here before.',
                    ],
                    techTable: {
                        columns: ['Action', 'When Used', 'Example'],
                        rows: [
                            ['Delete', 'The value has no use even anonymized.', 'An authorization header.'],
                            ['Hash (SHA-256)', 'Analytical value is worth preserving without exposing the real value.', 'An email address.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Nine distinct, named categories',
                            body: 'Email, phone, SSN, credit card, IP address, AWS keys, JWT tokens, vendor API keys (OpenAI/GitHub/Slack), and a generic catch-all — not a vague "looks sensitive" heuristic.',
                        },
                        {
                            title: 'Two genuinely different actions',
                            body: 'Delete for values with zero use even hashed, hash for values worth keeping analytically useful without exposing the real content.',
                        },
                        {
                            title: 'One computed "every category" source, not four hand-copied lists',
                            body: 'The specific fix for a real, documented incident where a newly-added category could silently miss one of several redaction call sites.',
                        },
                        {
                            title: 'Every mandatory redaction path shares that source',
                            body: 'The audit trail, escalation memory, the live UI stream, and the trace exporter all redact identically, not independently.',
                        },
                        {
                            title: 'Vendor API keys get their own dedicated category',
                            body: 'A distinguishing prefix plus a fixed-format body, treated the same way as a cloud provider\'s own access keys rather than folded into a vague generic bucket.',
                        },
                    ],
                    whyItMatters:
                        'Redaction that misses even one leak path, or that has to be remembered and re-added every time a new sensitive pattern comes up, is a false sense of protection. One shared, computed source of truth for "everything that must always be redacted" is what makes it structurally hard to repeat the exact mistake that happened here once already — not just a policy that depends on everyone remembering.',
                },
            },
            {
                id: 'cost-governance',
                name: 'Cost Governance',
                status: 'built',
                exec: 'Real dollar spend is tracked continuously across the whole deployment over three rolling time windows, with hysteresis-based alerting — a genuinely different mechanism from Context Budget\'s per-conversation token ceiling, not the same tracker reused.',
                eng: 'BudgetTrackingService tracks cumulative USD spend against daily/weekly/monthly thresholds with a hysteresis state machine (separate escalate/recover thresholds), publishing live ObservableGauge metrics for Prometheus.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Every charge',
                            text: 'Spend across the whole deployment is tracked continuously in real dollars — not tokens, and not just for one conversation, but rolled up across everyone using the system.',
                        },
                        {
                            time: 'Three clocks at once',
                            text: 'A daily total, a weekly total, and a monthly total each run independently against their own configured budget — so "today alone is unusually expensive" and "we\'re on pace to blow the monthly budget" are caught separately.',
                        },
                        {
                            time: 'Right at the edge',
                            text: 'Spend crosses a warning threshold and the status flips to Warning. A moment later, a small charge nudges the total back down slightly — but the status doesn\'t immediately flip back to Clear. It stays Warning until spend drops meaningfully below the line, not just barely under it, so the alert doesn\'t flap on and off every time the total wobbles near the threshold.',
                        },
                        {
                            time: 'A new period begins',
                            text: 'Each window\'s tracked total resets cleanly at its own natural boundary, and its status can genuinely return to Clear.',
                        },
                    ],
                    flow: [
                        { title: 'Record Spend In Real Dollars', tone: 'info', body: 'Not an estimate, not a token count converted later — an actual per-charge dollar amount.' },
                        { title: 'Track Three Windows At Once', tone: 'jargon', body: 'Daily, weekly, and monthly totals run independently, each against its own configured limit.' },
                        { title: 'Escalate At One Threshold, Recover At A Lower One', tone: 'warn', body: 'Going from Clear to Warning and going back from Warning to Clear use two different thresholds, on purpose.' },
                        { title: 'Roll Over Automatically', tone: 'tip', body: 'Each period\'s total resets cleanly at its own natural boundary.' },
                    ],
                    narrative: [
                        'This is distinct from Context Budget, which caps how many tokens one specific conversation can use. Cost Governance is a completely different concern — real dollar spend across the whole deployment, watched over three separate rolling time windows at once, for the kind of question someone running the deployment actually asks: are we on pace to blow through this month\'s budget, not just this one conversation\'s.',
                        'What\'s real, and a genuinely clever detail: the alert state doesn\'t just flip on and off at a single number. Crossing <mark class="hl">up</mark> into a warning or critical state happens at one threshold; coming back <mark class="hl">down</mark> to normal requires spend to drop meaningfully below that same threshold, not just barely under it. Without that gap, a total sitting right at the line would flip back and forth on every small charge — and an alert that fires that often gets ignored.',
                        '<mark class="hl">Three genuinely independent windows</mark>, not one number reused three ways — a deployment can be perfectly fine on its daily total while its monthly total is already flashing critical, and catching that distinction is exactly the point.',
                    ],
                    techTable: {
                        columns: ['Transition', 'Trigger'],
                        rows: [
                            ['Clear → Warning', 'Spend crosses the warning threshold.'],
                            ['Warning → Clear', 'Spend drops meaningfully below the warning threshold — not just under it.'],
                            ['Warning → Critical', 'Spend crosses the critical threshold.'],
                            ['Critical → Warning', 'Spend drops below the critical threshold but still above the recovery line.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Real dollar tracking, not a token proxy',
                            body: 'Spend is recorded and compared against budgets in actual USD.',
                        },
                        {
                            title: 'Three independent rolling windows',
                            body: 'Daily, weekly, and monthly totals are tracked and evaluated separately, each against its own configured limit.',
                        },
                        {
                            title: 'Hysteresis prevents alert flapping',
                            body: 'The threshold to escalate a status and the threshold to recover from it are deliberately different values, not the same line crossed in both directions.',
                        },
                        {
                            title: 'Each window rolls over on its own boundary',
                            body: 'A new day, week, or month resets that window\'s total independently of the others.',
                        },
                        {
                            title: 'Exposed as live gauges for monitoring',
                            body: 'Current spend and status per period are published as metrics a dashboard can scrape continuously, not just logged after the fact.',
                        },
                    ],
                    whyItMatters:
                        'Knowing you\'re spending too much only after the invoice arrives is too late to do anything about it. Watching real spend continuously across multiple time horizons, with an alerting mechanism specifically designed not to cry wolf every time a total wobbles near a threshold, is what turns "we should probably keep an eye on cost" into something someone can actually act on before the bill is a surprise.',
                },
            },
        ],
    },
    {
        id: 'tool-integration-surface',
        number: '06',
        name: 'Tool & Integration Surface',
        subtitle: 'How agents act on the world',
        boxes: [
            {
                id: 'mcp-server',
                name: 'MCP Server',
                status: 'built',
                exec: 'Lets other AI agents call into this harness’s own capabilities over a standard web connection, with real authentication and rate limits.',
                eng: 'ASP.NET Core WebAPI: .WithHttpTransport().LoadTools().LoadPrompts().LoadResources(), JWT Bearer auth via Entra ID.',
                docLink: '../08-mcp.html',
                deepDive: {
                    scenario: [
                        {
                            time: 'A completely separate tool connects',
                            text: 'An IDE, another agent, an automation pipeline — something that wants to use this harness\'s own skills and tools as if they were its own. It connects using the same open protocol MCP-compatible tools already speak to each other, not a bespoke integration.',
                        },
                        {
                            time: 'Before the server even starts',
                            text: 'It checks that a real authentication method is actually configured. If none is set up and nobody explicitly said "allow anonymous access," the whole server refuses to start — even in local development, where most systems would quietly let that slide.',
                        },
                        {
                            time: 'If anonymous access is genuinely wanted',
                            text: 'That has to be an explicit, visible opt-in written directly into the routing, not a silently-weaker default policy nobody would notice.',
                        },
                        {
                            time: 'A burst of requests arrives',
                            text: 'From more than one connected client at once. The rate limit that kicks in doesn\'t distinguish between them — it\'s one shared bucket for the whole server, not a separate allowance per client.',
                        },
                    ],
                    flow: [
                        { title: 'Speak The Real Protocol', tone: 'info', body: 'External tools connect using the actual open MCP protocol, not a custom API this harness invented.' },
                        { title: 'Refuse To Start Insecure', tone: 'warn', body: 'If no real authentication is configured, the server won\'t even boot, regardless of environment.' },
                        { title: 'Make Anonymous Access Visible, Not Default', tone: 'jargon', body: 'Allowing unauthenticated access is a deliberate, explicit choice in the routing code, never a fallback.' },
                        { title: 'Rate-Limit The Whole Server, Not Per Client', tone: 'tip', body: 'One shared limit protects the server as a whole, currently without distinguishing which client is responsible for the load.' },
                    ],
                    narrative: [
                        'This turns the harness into a genuine provider in the broader MCP ecosystem — any compatible external tool can connect and use its skills, tools, prompts, and resources through the same standard protocol, not a bespoke integration built for one consumer.',
                        'What\'s real, and a genuinely strict default: this server checks its own authentication configuration at startup and <mark class="hl">refuses to run at all</mark> if nothing real is configured and nobody explicitly opted into anonymous access — in every environment, including local development, where most systems would quietly relax that requirement.',
                        'One honest limitation worth knowing: the rate limit protecting this server is <mark class="hl">a single shared bucket for the whole server, not a separate allowance per connected client</mark> — so a hundred requests a minute is the total budget across everyone talking to it right now, not a hundred each.',
                    ],
                    techTable: {
                        columns: ['Auth Mode', 'When It Applies'],
                        rows: [
                            ['API key', 'A static, pre-shared key.'],
                            ['Static bearer token', 'A fixed, configured token.'],
                            ['Entra ID (JWT)', 'A real identity provider.'],
                            ['Explicit anonymous opt-in', 'Only when deliberately enabled — visible in the routing itself, never a silent fallback.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Refuses to start without real authentication',
                            body: 'The check runs at startup, in every environment — not just production.',
                        },
                        {
                            title: 'Anonymous access is a visible, explicit code path',
                            body: 'A deliberate opt-in call, not something that happens by omission.',
                        },
                        {
                            title: 'Rate limiting is currently one shared bucket',
                            body: 'The partition key is a fixed string, not the caller\'s identity, so the limit is a total across every connected client combined, not per-client.',
                        },
                        {
                            title: 'Exposes the harness\'s own skill catalog as real MCP tools',
                            body: 'Listing, fetching, and searching skills are genuine callable tools external systems can invoke, not just documentation.',
                        },
                        {
                            title: 'Runs as its own separate process',
                            body: 'A standalone ASP.NET Core app, distinct from the harness\'s other hosts, that can also be embedded as a sub-application.',
                        },
                    ],
                    whyItMatters:
                        'Exposing a harness\'s real capabilities to the outside world only makes sense if the exposure itself doesn\'t become the weak point. Refusing to boot without real authentication, and making an anonymous-access decision something a person has to deliberately write down rather than something that just happens quietly, is what keeps "open to the MCP ecosystem" from also meaning "open to anyone."',
                },
            },
            {
                id: 'mcp-client',
                name: 'MCP Client',
                status: 'built',
                exec: 'Connects out to tools hosted anywhere else and makes them available to the agent exactly like a built-in tool.',
                eng: 'Discovers tools via tools/list at startup; supports HTTP or stdio transport and Bearer/Entra/ApiKey outbound authentication.',
                docLink: '../08-mcp.html',
                techs: ['HTTP Transport', 'stdio Transport'],
            },
            {
                id: 'api-hub',
                name: 'API Hub',
                status: 'built',
                exec: 'The same Execution API listed under User Surface — it’s both an entry point for humans and an integration surface for other systems.',
                docLink: '../17-bundle-api.html',
            },
            {
                id: 'a2a-outbound',
                name: 'A2A Outbound',
                status: 'built',
                exec: 'This harness can call out to other agents using the Agent-to-Agent protocol, carrying its own identity with it.',
                eng: 'A2AIdentityPropagator handles outbound identity propagation, the mirror of the inbound path under User Surface.',
            },
            {
                id: 'code-sandbox',
                name: 'Code Sandbox',
                status: 'built',
                exec: 'When an agent needs to actually run code, it happens in an isolated box that can’t touch anything it wasn’t explicitly allowed to.',
                eng: 'ProcessSandboxExecutor (Windows Job Objects) and DockerSandboxExecutor, both with HMAC attestation and a closed-by-default capability model.',
            },
            {
                id: 'computer-use',
                name: 'Computer Use',
                status: 'not-built',
                exec: 'Not built. No browser/desktop-automation tool exists in this repo.',
            },
        ],
    },
    {
        id: 'memory-knowledge',
        number: '07',
        name: 'Memory & Knowledge',
        subtitle: 'What the platform remembers',
        boxes: [
            {
                id: 'episodic-memory',
                name: 'Episodic Memory',
                status: 'partial',
                exec: 'Real memory of past interactions exists and fades over time, just not organized under this exact name.',
                eng: 'KnowledgeMemoryService’s Remember/Recall/Forget/Improve, with MemoryDecayService applying exponential weight decay (5% per day untouched, by default) and pruning below a threshold.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Mon',
                            text: 'A user mentions, mid-conversation, that their team is migrating off a legacy system. Nothing about the current task depends on it — but it’s worth keeping.',
                        },
                        {
                            time: '+0s',
                            text: 'Before it’s kept anywhere, the fact is screened — the same way an email attachment gets scanned before you trust it. Nothing looks like an attempt to plant a hidden instruction, so it’s marked trustworthy and filed away.',
                        },
                        {
                            time: 'Day 1–20',
                            text: 'Nobody asks about it again. Like an unused muscle, its importance quietly fades a little each day it goes untouched.',
                        },
                        {
                            time: 'Day 21',
                            text: 'In a completely different conversation, the user asks something related. The fact resurfaces automatically — no one had to remind the agent it existed.',
                        },
                        {
                            time: 'Day 21',
                            text: 'Being recalled resets its clock — it’s relevant again, so it stops fading for now.',
                        },
                    ],
                    flow: [
                        { title: 'Remember', tone: 'info', body: 'A fact worth keeping shows up in conversation.' },
                        {
                            title: 'Safety Check',
                            tone: 'warn',
                            body: 'Screened before anything is trusted. If it passes, it’s stored and recallable. If it looks suspicious, it’s quarantined — kept for audit, but never served back to the agent.',
                        },
                        { title: 'Fades Over Time', tone: 'jargon', body: 'Loses roughly 5% of its importance per day it goes untouched, until it’s pruned entirely.' },
                        { title: 'Recall', tone: 'tip', body: 'A fast cache is checked first; a fuller search runs only if that’s not enough. Being recalled resets the fade.' },
                    ],
                    /* Each paragraph is trusted, hand-authored HTML (not escaped at render time) so
                       key phrases can be wrapped in <mark class="hl"> to guide a skimming reader's
                       eye straight to what matters, instead of a flat wall of text. */
                    narrative: [
                        'Episodic memory usually means: <mark class="hl">memory of a specific thing that happened at a specific time</mark> — "the user told me X on Tuesday" — as opposed to semantic memory (general facts) or procedural memory (how to do something).',
                        'What’s real: a genuine memory system — remember a fact, recall it later, forget it, and improve it based on feedback. Two things make it more than a toy. Every memory is <mark class="hl">safety-scanned before it’s trusted</mark>: before anything gets remembered, it passes through a gate that scans for injected instructions and stamps where the fact came from. If something looks untrustworthy, it still gets written for audit purposes, but it’s <mark class="hl">quarantined and can never be served back to the agent</mark> — a real security property most memory systems skip entirely. And <mark class="hl">memories fade on purpose</mark>: each fact carries a weight that decays smoothly over time and is deleted outright once it drops below a threshold — like human memory, not everything is kept with equal weight.',
                        'Why it’s <mark class="hl">"partial," not "built"</mark>: the system doesn’t actually distinguish "this happened at a specific moment" from "this is just a general fact worth knowing." It’s <mark class="hl">one undifferentiated bucket</mark> of remembered facts, tagged with a free-text label, not a real timeline of events.',
                    ],
                    techTable: {
                        columns: ['Mode', 'What It Does', 'Default?'],
                        rows: [
                            ['Legacy recall', 'Substring match against the session cache, then a graph traversal.', 'Yes'],
                            ['Harmonic recall', 'Matches by meaning (an abstraction + cue anchors), and can merge near-duplicate memories instead of creating new ones.', 'No — off by default'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Four real operations',
                            body: '<code>KnowledgeMemoryService</code> implements Remember, Recall, Forget, and Improve — not just store-and-fetch.',
                        },
                        {
                            title: 'The safety gate',
                            body: 'Every write passes through <code>IMemoryWriteGate</code> first. An untrusted fact is still saved for audit, but is filtered out of every future recall, permanently.',
                        },
                        {
                            title: 'Two-speed recall',
                            body: 'The in-session cache is checked first (sub-millisecond); a full graph search only runs if that’s not enough.',
                        },
                        {
                            title: 'The decay formula',
                            body: 'Weight decays as <code>weight × (1 − rate)^days</code> since last access (default rate: 5%/day), with a separate pass pruning nodes below a threshold.',
                        },
                        {
                            title: 'Scoped by user',
                            body: 'Keys are namespaced by tenant and user, so recall can never cross a scope boundary even by accident.',
                        },
                    ],
                    whyItMatters:
                        'This is what lets an agent feel like it "remembers you" across sessions — without either forgetting everything the moment a conversation ends, or turning into a liability that will happily remember whatever a bad actor tries to plant. Getting both right at once is the actual hard part; most systems pick one.',
                },
            },
            {
                id: 'semantic-memory',
                name: 'Semantic Memory',
                status: 'partial',
                exec: 'A background process pulls durable facts out of what you say and remembers them — just not as a separate memory type from episodic events.',
                eng: 'ConversationFactExtractor runs an LLM-based extraction after each turn; facts above a confidence bar are written through the same KnowledgeMemoryService.RememberAsync pipeline Episodic Memory uses, tagged as a fact instead of an event.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Turn 1',
                            text: 'A user mentions, in passing, "I\'m the CTO here, and I really don\'t need the long version — just tell me what changed."',
                        },
                        {
                            time: '+0s',
                            text: 'The turn finishes and the response goes back to the user immediately. Separately, and without holding anything up, a second, cheaper model reads that same exchange looking for facts worth keeping.',
                        },
                        {
                            time: '+0s',
                            text: 'It pulls out two candidates: "user\'s role is CTO" and "user prefers terse updates." A third guess — "may also manage the infrastructure budget" — comes back too uncertain and is thrown away before it\'s ever stored.',
                        },
                        {
                            time: 'Weeks later',
                            text: 'In a completely different conversation, the user asks for a project update. The answer comes back short and to the point — that preference resurfaced automatically, blended in with whatever else the agent remembers.',
                        },
                    ],
                    flow: [
                        { title: 'Extract', tone: 'info', body: 'After every successful turn, a background call to a cheaper model reads the exchange and pulls out discrete, structured facts — not just the raw transcript.' },
                        {
                            title: 'Confidence Filter',
                            tone: 'warn',
                            body: 'Each candidate fact comes back with a confidence score. Anything underneath the line is silently dropped before it\'s ever written down.',
                        },
                        { title: 'Remember', tone: 'jargon', body: 'What survives is filed through the exact same pipeline Episodic Memory uses — the same safety scan, the same decay — just tagged "Fact" instead of an event.' },
                        { title: 'Recall', tone: 'tip', body: 'Later, recall doesn\'t distinguish "a fact about you" from "something that happened" — it returns whichever is relevant, from the same undifferentiated bucket.' },
                    ],
                    narrative: [
                        'Semantic memory usually means <mark class="hl">a durable fact about the world, detached from the moment you learned it</mark> — "the user is a CTO," not "the user told me on Tuesday they\'re a CTO." That second, timestamped version is episodic memory; the two are supposed to be different systems.',
                        'What\'s real: after every successful turn, a <mark class="hl">background call to a cheaper model reads the conversation and pulls out discrete facts</mark> — not the raw text, but short, structured claims, each with a confidence score attached. Anything below the confidence bar is <mark class="hl">thrown away before it\'s ever stored</mark>, and the whole extraction step runs fire-and-forget on its own timer, so it never adds latency to the response the user is waiting for.',
                        'Why it\'s <mark class="hl">"partial," not "built"</mark>: once a fact survives, it isn\'t stored anywhere distinct from an event — it\'s written into <mark class="hl">the exact same bucket as Episodic Memory</mark>, just with a different label. And the dial that\'s supposed to tune how strict the confidence bar is exists in configuration, but <mark class="hl">isn\'t actually wired to the code that filters facts</mark> — turning it up or down currently does nothing.',
                    ],
                    techTable: {
                        columns: ['Source', 'How It Gets In', 'Scope'],
                        rows: [
                            ['Conversation facts', 'Pulled out of your own turns by a background model call, tagged with a type and a confidence score.', 'Yours alone — decays and can be forgotten like any other memory.'],
                            ['Corpus entities', 'Extracted while ingesting documents into the knowledge graph — people, organizations, technologies.', 'Shared across your tenant, deduplicated by name and type.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'One extra model call per turn',
                            body: '<code>ConversationFactExtractor</code> runs a dedicated prompt through the cheapest available model tier after each successful turn — fire-and-forget, never blocking the response.',
                        },
                        {
                            title: 'The confidence knob is disconnected',
                            body: 'A configurable minimum-confidence setting exists (<code>KnowledgeBridgeConfig.MinConfidence</code>), but the extractor filters against its own fixed 0.7 constant instead — changing the setting has no effect today.',
                        },
                        {
                            title: 'Facts and events share one bucket',
                            body: 'A kept fact is written through the identical Remember call Episodic Memory uses, just tagged "Fact" — same decay curve, same safety gate, same graph.',
                        },
                        {
                            title: 'Off by default',
                            body: 'The whole pipeline sits behind one switch, disabled out of the box, so a freshly cloned template never starts remembering things about its users silently.',
                        },
                        {
                            title: 'Entities are a separate, already-distinct system',
                            body: 'Facts from conversation and entities from ingested documents both land in the graph, but through two different paths with different sharing rules — one private and per-user, one shared and deduplicated tenant-wide.',
                        },
                    ],
                    whyItMatters:
                        'This is the difference between an agent that remembers you said something and one that actually knows something about you. The gap isn\'t the idea — the extraction, the confidence gate, the decay are all real and running on every turn. It\'s finishing the separation: giving facts their own store instead of folding them into the same bucket as raw events, and actually wiring the confidence knob that\'s already been built.',
                },
            },
            {
                id: 'procedural-memory',
                name: 'Procedural Memory',
                status: 'not-built',
                exec: 'Not built. The harness doesn’t separately store "how to do X" procedures distinct from its other memory types.',
            },
            {
                id: 'vector-store',
                name: 'Vector Store',
                status: 'built',
                exec: 'Documents are converted into a searchable form based on meaning, not just keywords.',
                eng: 'Dense (vector) retrieval fused with sparse (BM25) via Reciprocal Rank Fusion; backend is swappable (see Retrieval Backend under RAG Pipeline).',
                deepDive: {
                    scenario: [
                        {
                            time: 'Query',
                            text: 'Someone asks the assistant, "how much time off do I get?" The company\'s actual policy document never uses the words "time off" — it says "PTO accrual."',
                        },
                        {
                            time: '+0s',
                            text: 'Two searches run against the same documents at the same time: one looks for passages that mean the same thing, the other looks for passages that use the same words.',
                        },
                        {
                            time: '+0s',
                            text: 'The meaning-based search finds the PTO policy even though no words match. A separate search, a moment later, is asked something with an exact acronym in it — and the keyword side catches that one instead, because meaning-based search alone can blur past precise terms.',
                        },
                        {
                            time: '+0s',
                            text: 'Both rankings are blended into one list mathematically, rather than picking a winner, so the final answer is grounded in whichever passage actually was the better match — regardless of which of the two searches found it.',
                        },
                    ],
                    flow: [
                        { title: 'Embed', tone: 'info', body: 'The question is converted into the same kind of "meaning fingerprint" the documents were converted into ahead of time.' },
                        { title: 'Search Twice, At Once', tone: 'jargon', body: 'A meaning-based search and a traditional keyword search run concurrently against the same documents — neither waits on the other.' },
                        { title: 'Fuse The Rankings', tone: 'tip', body: 'The two ranked lists are mathematically combined into one, so a passage that scores well on either search counts, not just the ones both agree on.' },
                        { title: 'Degrade, Don\'t Fail', tone: 'warn', body: 'If one of the two searches errors out, the answer still comes back using whichever search still worked — the whole request doesn\'t fail because of one broken piece.' },
                    ],
                    narrative: [
                        'A vector store\'s job is to let search work on <mark class="hl">meaning instead of exact wording</mark> — a question about "time off" should find a document that only ever says "PTO," the way a person would, instead of missing it because the words don\'t literally match.',
                        'What\'s real: every search actually runs <mark class="hl">two retrieval methods at once</mark> — a meaning-based one and a traditional keyword one — and <mark class="hl">mathematically blends their two rankings into one</mark> rather than trusting either alone. That matters because meaning-based search can blur past an exact acronym or ID number a keyword search would catch instantly, and a pure keyword search misses paraphrasing entirely. If either side fails, the system quietly <mark class="hl">falls back to whichever one still works</mark> instead of failing the whole request. The storage layer underneath is also <mark class="hl">swappable behind one setting</mark> — a fully local, self-hosted stack or a managed cloud search service, without touching any calling code.',
                        'This one is marked <mark class="hl">"Built," not "Partial"</mark> — unlike memory, there\'s no undifferentiated bucket or disconnected setting here. The blending math, the concurrent execution, and the graceful fallback are all real, all wired up, and all running on every retrieval call.',
                    ],
                    techTable: {
                        columns: ['Provider', 'Meaning-Based Search', 'Keyword Search', 'Where It Runs'],
                        rows: [
                            ['Azure AI Search', 'Native managed vector index', 'Native managed keyword ranking', 'Microsoft-hosted cloud service'],
                            ['FAISS + SQLite (default)', 'In-process vector index', 'SQLite full-text index', 'Local — no external service required'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Both searches run concurrently',
                            body: 'The meaning-based and keyword searches fire at the same time and are awaited together, so blending them costs no extra latency over running just one.',
                        },
                        {
                            title: 'The blending formula is explicit',
                            body: 'Reciprocal Rank Fusion scores each result as <code>1/(k + rank)</code> summed across both searches (default <code>k = 60</code>) — a well-known, tunable formula, not an ad hoc heuristic.',
                        },
                        {
                            title: 'Each side can fail without sinking the request',
                            body: 'If the meaning-based search throws, the system logs a warning and serves keyword-only results, and vice versa — a broken dependency degrades quality, it doesn\'t return an error page.',
                        },
                        {
                            title: 'Over-fetches before fusing',
                            body: 'Each search is asked for 3x the number of results actually needed, so there\'s enough overlap between the two rankings for the fusion math to be meaningful.',
                        },
                        {
                            title: 'One config value swaps the whole backend',
                            body: 'Switching from the local stack to Azure AI Search — or back — is a single provider setting; the retrieval code that calls it never changes.',
                        },
                    ],
                    whyItMatters:
                        'Grounding an answer in real documents only works if the right passage gets found in the first place. A system that only understands meaning misses exact terms; one that only matches keywords misses paraphrasing. Blending both, with one covering for the other when something breaks, is the difference between retrieval that works in a demo and retrieval that holds up when someone asks a question the way people actually talk.',
                },
            },
            {
                id: 'knowledge-graph',
                name: 'Knowledge Graph',
                status: 'built',
                exec: 'Beyond documents, the harness can build and remember a map of entities and how they relate — who worked on what, what depends on what — so understanding compounds across sessions.',
                eng: 'Neo4j or PostgreSQL in production (in-memory for dev/test) behind one interface, Leiden community detection for graph-RAG, feedback-weighted retrieval, TenantIsolatedGraphStore for multi-tenant isolation.',
                docLink: '../07-rag.html',
                techs: ['Neo4j', 'Kuzu', 'PostgreSQL', 'In-memory (dev/test)'],
                deepDive: {
                    scenario: [
                        {
                            time: 'Ingestion',
                            text: 'A batch of internal documents gets pulled in. Along the way, the system notices "Meridian," "Acme Corp," and a person\'s name showing up together across several of them, and quietly records that they\'re connected — not just that they appear in the same paragraph.',
                        },
                        {
                            time: 'Later',
                            text: 'Someone asks, "what\'s the status of Meridian?" Instead of returning one paragraph that happens to mention the word, the system can follow the relationship — Meridian, who owns it, what it depends on — to assemble a fuller answer.',
                        },
                        {
                            time: 'Next message',
                            text: 'The user replies, "no, that\'s the old vendor, we switched in March." Nobody clicked a thumbs-down. But the system reads that reaction and quietly treats whatever it just relied on as a little less trustworthy going forward.',
                        },
                        {
                            time: 'Meanwhile',
                            text: 'A second company using the same deployment has its own "Meridian" — a completely different project. Its data never mixes with the first company\'s, even though both are running through the identical graph.',
                        },
                    ],
                    flow: [
                        { title: 'Extract & Link', tone: 'info', body: 'While ingesting content, entities and the relationships between them are pulled out and written down — not just the documents themselves.' },
                        { title: 'Group Into Communities', tone: 'jargon', body: 'Related entities are clustered together, so the system can reason and summarize at the level of a topic instead of one disconnected fact at a time.' },
                        { title: 'Listen For Reaction', tone: 'tip', body: 'The user\'s very next message is read for satisfaction or frustration — no rating widget required.' },
                        { title: 'Reweight', tone: 'warn', body: 'That implied reaction nudges how much the graph trusts whatever it just used, shifting future answers without anyone touching a setting.' },
                    ],
                    narrative: [
                        'A knowledge graph is <mark class="hl">a map of things and how they relate</mark>, not just a pile of documents — so an answer can follow a connection ("who owns this, what does it depend on") instead of stopping at whichever single paragraph happened to mention the right word.',
                        'What\'s real: entities and relationships are genuinely extracted and linked as content comes in, then <mark class="hl">clustered into higher-level groups</mark> so the system can summarize a whole topic instead of reasoning fact-by-fact. Isolation between tenants is enforced <mark class="hl">record by record, not all-or-nothing</mark> — two customers can run on the same deployment and never see each other\'s private data, while still sharing a common pool of general knowledge. And the feedback loop is genuinely quiet: it <mark class="hl">reads the user\'s next message for signs of satisfaction or frustration</mark> rather than requiring an explicit rating, and nudges the graph\'s confidence accordingly.',
                        'One honest caveat: that feedback signal, by default, lives in <mark class="hl">fast in-memory storage, not the same durable database as the graph itself</mark> — restart the process and the graph survives, but everything it had learned from user reactions resets to neutral. The code says as much in its own comments; swapping in durable storage for it is a deliberate, documented next step, not a hidden gap.',
                    ],
                    techTable: {
                        columns: ['Backend', 'Best For', 'Notes'],
                        rows: [
                            ['Neo4j', 'Production, dedicated graph workloads', 'Purpose-built graph database with native relationship traversal.'],
                            ['Kuzu', 'Production without running a separate server', 'Embedded graph database — ships inside the process.'],
                            ['PostgreSQL', 'Reusing infrastructure you already run', 'One less system to operate if Postgres is already in your stack.'],
                            ['In-memory', 'Local development and tests only', 'Nothing persists across a restart.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Isolation is checked per record',
                            body: 'Every read checks both tenant AND owner on that specific node — a user sees their tenant\'s shared knowledge plus their own private facts, never anyone else\'s.',
                        },
                        {
                            title: 'Feedback comes from conversation, not a rating widget',
                            body: 'An economy-tier model reads the user\'s next message to detect implicit satisfaction or frustration — no explicit thumbs-up/down UI exists or is required.',
                        },
                        {
                            title: 'Reweighting is a fast running average',
                            body: 'Feedback blends into a node or edge\'s trust score via an exponential moving average — a cheap incremental update, not a full recomputation.',
                        },
                        {
                            title: 'Community detection is "Leiden-inspired," not the textbook algorithm',
                            body: 'The clustering step is a simplified approximation of the published Leiden method, documented as such — a reasonable engineering tradeoff, not a case of overclaiming.',
                        },
                        {
                            title: 'The feedback store defaults to in-memory',
                            body: 'Its own code comments flag this: production deployments should back it with the same durable database as the graph, which isn\'t wired up by default.',
                        },
                    ],
                    whyItMatters:
                        'An assistant that only retrieves paragraphs treats every document as unrelated to every other one, and never gets better at knowing which ones to trust. A real graph can follow a relationship to assemble a fuller answer, and can quietly get better over time from how people actually react — not just from an explicit rating nobody bothers to leave. The gap worth watching is that "gets better over time" part currently forgets what it learned on every restart unless someone wires up durable storage for it.',
                },
            },
            {
                id: 'rag-pipeline',
                name: 'RAG Pipeline',
                status: 'built',
                exec: 'How the agent answers questions grounded in your real documents instead of guessing — finds the genuinely relevant passages first, then answers from those, with citations.',
                eng: 'Five-stage pipeline: ingestion, query transformation (RAG Fusion, HyDE), hybrid retrieval, CRAG quality evaluation, token-budgeted assembly with citations.',
                docLink: '../07-rag.html',
                techs: ['Dense + BM25 Hybrid (default)', 'Azure AI Search (agentic retrieval)', 'FAISS', 'SQLite FTS5'],
                deepDive: {
                    scenario: [
                        {
                            time: 'Question asked',
                            text: 'A user asks something that\'s actually answerable from the company\'s documents. The retrieved passages come back strong, and the answer is assembled and returned with citations — the fast path, and the common case.',
                        },
                        {
                            time: 'A harder question',
                            text: 'This time, what comes back is borderline — technically related, but not a confident match. Instead of answering from weak material, the system quietly rewrites the question and tries retrieval again.',
                        },
                        {
                            time: 'Still not enough',
                            text: 'The second attempt still isn\'t good enough, and retries are exhausted. Rather than presenting an answer built on shaky evidence, the system explicitly reports that it couldn\'t find relevant material — instead of guessing anyway.',
                        },
                        {
                            time: 'A different case',
                            text: 'A question turns out to genuinely be about something outside the ingested documents entirely. Here, the same quality check instead triggers a live web search to fill the gap, if that fallback is turned on.',
                        },
                    ],
                    flow: [
                        { title: 'Retrieve & Rerank', tone: 'info', body: 'Candidate passages come back from hybrid search, then get reordered by a more careful relevance pass before anything is judged.' },
                        { title: 'Grade The Evidence', tone: 'jargon', body: 'A dedicated quality check scores how relevant what was retrieved actually is to the question — before any answer gets built from it.' },
                        { title: 'Accept, Retry, Fall Back, or Refuse', tone: 'warn', body: 'Good evidence gets used. Borderline evidence triggers a rewritten retry (capped, so it can\'t loop forever). Weak evidence either falls back to a web search or is explicitly rejected — never silently guessed past.' },
                        { title: 'Assemble With Citations', tone: 'tip', body: 'What survives is packed into a token budget and handed back with citations pointing to exactly which passages the answer came from.' },
                    ],
                    narrative: [
                        'The core promise of this kind of system is <mark class="hl">answering from evidence instead of guessing</mark> — find the genuinely relevant material first, then answer only from that, and show your work with citations.',
                        'What\'s real: retrieval doesn\'t just run once and hope. A dedicated quality check <mark class="hl">grades how relevant the retrieved passages actually are</mark> before anything gets built from them, and that grade drives one of four distinct outcomes — accept the evidence, quietly rewrite the question and try again (capped at a couple of attempts so it can\'t loop forever), fall back to a live web search if that\'s enabled, or <mark class="hl">explicitly refuse to answer rather than guess past weak evidence</mark>. Harder, multi-part questions get their own path — the system can retrieve <mark class="hl">in several rounds instead of one</mark>, gathering more evidence between rounds when a single pass wasn\'t enough.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — the grading step, the four-way branching, the retry cap, and the citation-backed final answer are all real, all wired together, and all running on every question that reaches this pipeline.',
                    ],
                    techTable: {
                        columns: ['Stage', 'What It Does'],
                        rows: [
                            ['Query transformation', 'Rewrites or expands the question before searching — including generating a hypothetical ideal answer and searching for passages that resemble it, which often finds better matches than searching with the raw question.'],
                            ['Hybrid retrieval', 'Meaning-based and keyword search run together and get blended (see Vector Store).'],
                            ['Quality evaluation', 'Grades the retrieved evidence and decides: accept, retry with a better question, fall back to the web, or refuse.'],
                            ['Assembly', 'Packs what survives into a fixed token budget and attaches citations to the specific source passages.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Four distinct outcomes, not pass/fail',
                            body: 'The quality check returns one of Accept, Refine, Reject, or WebFallback — a real decision tree, not a single relevance threshold.',
                        },
                        {
                            title: 'Refinement is capped',
                            body: 'A borderline result triggers a rewritten retry, but only up to a fixed retry limit — it degrades to "best available" rather than retrying indefinitely.',
                        },
                        {
                            title: 'Rejection is a real, visible outcome',
                            body: 'When evidence is judged irrelevant, the pipeline returns an explicit "nothing relevant found" result instead of assembling an answer from weak material anyway.',
                        },
                        {
                            title: 'Complex questions get multi-round retrieval',
                            body: 'Harder queries route to an iterative retriever that can fetch evidence across several rounds instead of a single retrieval pass, checked afterward by a separate faithfulness evaluator.',
                        },
                        {
                            title: 'A missing evaluator fails safe, not open',
                            body: 'If the quality check itself can\'t run, that\'s treated as "no confident judgment" and handled the same as an exhausted retry — never silently treated as a passing grade.',
                        },
                    ],
                    whyItMatters:
                        'The difference between a demo and something people can actually rely on is what happens when the first search doesn\'t find a good answer. A system that always just answers with whatever it retrieved will confidently answer questions it has no real basis for. This one grades its own evidence, tries again when it\'s not sure, reaches further when it genuinely needs to, and says so out loud when it can\'t find anything real — which is what makes the citations on a good answer actually mean something.',
                },
            },
        ],
    },
    {
        id: 'state-persistence',
        number: '08',
        name: 'State & Persistence',
        subtitle: 'Remembering across turns and restarts',
        boxes: [
            {
                id: 'conversation-history',
                name: 'Conversation History',
                status: 'built',
                exec: 'Conversations survive a restart — they’re not held only in memory.',
                eng: 'ConversationOrchestrator + durable conversation transcript, backed by EF Core.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Mid-conversation',
                            text: 'A user is a few turns into a conversation with the agent. The host process restarts — a deploy, a crash, anything. When they send their next message, the conversation continues exactly where it left off, not from scratch.',
                        },
                        {
                            time: 'At the same time',
                            text: 'Two different requests for that same conversation land on two different servers at almost the same moment. Without something to stop it, both would try to continue the conversation at once — each one blind to what the other just did.',
                        },
                        {
                            time: 'Instead',
                            text: 'One of the two requests waits its turn. The first finishes, writes its messages, and only then does the second one proceed — reading everything the first one just wrote, in order.',
                        },
                        {
                            time: 'If a host dies mid-turn',
                            text: 'Its claim on the conversation automatically expires rather than blocking it forever, so a crashed server can\'t permanently freeze someone else\'s conversation.',
                        },
                    ],
                    flow: [
                        { title: 'Claim The Turn', tone: 'info', body: 'Before touching a conversation, a request has to hold an exclusive claim on it — waiting in line if someone else already holds it.' },
                        { title: 'Read, Then Write', tone: 'jargon', body: 'The prior messages are loaded, the model responds, and the new turn is appended — durably, not just in the running process\'s memory.' },
                        { title: 'Release, Or Expire', tone: 'tip', body: 'The claim is released when the turn finishes. If the host holding it dies instead, the claim times out on its own rather than freezing the conversation.' },
                        { title: 'Ownership Checked Once, Centrally', tone: 'warn', body: 'Every read or write is checked against who actually owns the conversation — enforced in one place, not re-implemented at each call site.' },
                    ],
                    narrative: [
                        'The bar for "conversation history" isn\'t just saving messages — it\'s making sure two things happening on the same conversation at once <mark class="hl">can never silently corrupt it</mark>, whether they land on one server or two.',
                        'What\'s real: conversations are stored durably rather than only in a running process\'s memory, so a restart doesn\'t lose them. On top of that, a real coordination mechanism means <mark class="hl">only one turn can ever run against a conversation at a time</mark> — a second request arriving mid-turn waits, rather than the two interleaving and producing a transcript where messages land out of causal order. And if the host running a turn crashes mid-way, that claim <mark class="hl">expires on its own instead of freezing the conversation</mark> for everyone else. Ownership is enforced the same way everywhere: <mark class="hl">one check, in the storage layer itself</mark>, not something each new piece of code has to remember to re-verify.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — durability, the turn-claiming mechanism, and the ownership check are all real and all exercised on every conversation, on the default local storage.',
                    ],
                    techTable: {
                        columns: ['Storage Backend', 'Safe For', 'Caveat'],
                        rows: [
                            ['SQLite via EF Core (default)', 'Multiple server processes on one machine', 'Its safety comes from the database file\'s own locking — it doesn\'t extend across a network share to multiple machines.'],
                            ['One JSON file per conversation', 'Single-process local development only', 'Its write protection is an in-process lock; two hosts sharing the same file can produce a corrupted record.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The turn-claiming mechanism works across separate processes',
                            body: 'It\'s not just an in-memory lock — a claim taken by one server is visible to a completely different server process talking to the same storage.',
                        },
                        {
                            title: 'A lost claim is made visible, not silent',
                            body: 'If a host\'s claim on a conversation expires while it still believes it holds it (a stall, a crash, a paused process), that\'s surfaced explicitly rather than letting the host keep writing as if nothing changed.',
                        },
                        {
                            title: 'No guaranteed order between two waiting requests',
                            body: 'If two requests are queued for the same conversation at once, the system doesn\'t promise which one goes first across different servers — only that they can never run at the same time.',
                        },
                        {
                            title: 'One ownership check, not several',
                            body: 'The check for "does this caller actually own this conversation" lives in exactly one place in the storage layer. Earlier versions of this codebase had it hand-copied into as many as six separate call sites — now every one of them just relies on the store to refuse an unauthorized read or write.',
                        },
                        {
                            title: 'The storage layer and the turn-claiming mechanism are deliberately paired',
                            body: 'Swapping one without the other breaks: a durable claim expects to find the same record the durable store manages, and pairing a durable store with an in-memory claim only protects one server against itself.',
                        },
                    ],
                    whyItMatters:
                        'A chatbot that just appends messages to a list looks fine until two requests hit it at once, or the server restarts mid-conversation — and then it silently produces a transcript that doesn\'t reflect what actually happened. Making concurrency and durability correct together, instead of bolting durability onto something that already assumed a single process, is exactly the unglamorous work that determines whether a multi-server deployment behaves like one system or like several ones quietly stepping on each other.',
                },
            },
            {
                id: 'agent-state',
                name: 'Agent State',
                status: 'built',
                exec: 'An in-progress multi-step plan can be checkpointed and resumed rather than starting over.',
                eng: 'EfCorePlanStateStore persists PlanGraph execution state for PlanExecutor’s checkpoint/resume.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Step 4 of 7',
                            text: 'A multi-step plan is midway through. Steps 1 through 3 have genuinely finished, step 4 is actively running, and 5 through 7 haven\'t started yet. The process crashes — a deploy, an outage, anything.',
                        },
                        {
                            time: 'On restart',
                            text: 'The plan doesn\'t start over. Steps 1–3 stay marked done. Step 4 — the one that was actually running when everything stopped — is reset to "ready to try again," since nobody can know if it half-finished. Steps 5–7 are untouched.',
                        },
                        {
                            time: 'Resumed',
                            text: 'Execution picks back up from step 4 as if the crash were a hiccup, not a disaster. The plan\'s history records exactly when it resumed, for anyone auditing later.',
                        },
                        {
                            time: 'If step 4 then genuinely fails',
                            text: 'It\'s retried automatically, waiting a little longer between each attempt. Once its retry budget runs out, what happens next is a deliberate policy choice — not a hardcoded crash.',
                        },
                    ],
                    flow: [
                        { title: 'Checkpoint As You Go', tone: 'info', body: 'Each step\'s state is durably saved as it changes — not just held in the memory of whichever process happens to be running the plan.' },
                        { title: 'Crash Doesn\'t Mean Restart', tone: 'jargon', body: 'On resume, finished steps stay finished. Only the step that was actually running when things stopped gets reset to try again.' },
                        { title: 'Retry With Increasing Patience', tone: 'tip', body: 'A step that fails outright is retried automatically, waiting longer between each attempt, up to a configured limit.' },
                        { title: 'Choose What "Give Up" Means', tone: 'warn', body: 'Once retries are exhausted, the plan\'s own policy decides: fail just that step, skip it and move on, fail the whole plan, or hand it to a human.' },
                    ],
                    narrative: [
                        'The point of checkpointing a multi-step plan is that <mark class="hl">a crash costs at most the one step that was mid-flight</mark> — not the entire plan, and not silent data loss either.',
                        'What\'s real: every step\'s state is written durably as the plan runs, not just held in memory. On resume, the system doesn\'t naively replay everything — it specifically <mark class="hl">resets only the step that was still running when the process died</mark>, because that one is genuinely ambiguous, while everything already finished is trusted and skipped. Each resume is <mark class="hl">recorded in the plan\'s own history</mark>, so an interrupted-and-recovered plan is auditable, not indistinguishable from one that ran straight through. And failure handling isn\'t a single behavior: a step gets retried with a growing delay between attempts, and only once that budget runs out does the plan\'s own policy decide whether to <mark class="hl">fail just that step, skip it, fail the whole plan, or escalate to a person</mark>.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — the checkpointing, the resume-only-what-was-interrupted behavior, and all four failure policies are real and exercised on every plan run, not just the happy path.',
                    ],
                    techTable: {
                        columns: ['When Retries Run Out', 'What Happens'],
                        rows: [
                            ['Fail this step', 'Only this step is marked failed; anything that doesn\'t depend on it still runs.'],
                            ['Skip this step', 'The plan continues as if the step had completed.'],
                            ['Fail the whole plan', 'Execution stops immediately — used when the failed step is load-bearing for everything after it.'],
                            ['Escalate', 'Hands off to a human for manual intervention instead of failing automatically.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Only the interrupted step is reset on resume',
                            body: 'Genuinely completed steps are never re-run after a crash — only a step caught mid-execution is put back to "ready," because its true outcome is unknown.',
                        },
                        {
                            title: 'Resume is a distinct, logged event',
                            body: 'Resuming a plan writes its own entry to the plan\'s execution history, separate from loading its current state for a status check.',
                        },
                        {
                            title: 'Three backoff shapes for retries',
                            body: 'A failing step can be retried with a fixed delay, a linearly growing one, or an exponentially growing one — a configurable choice per plan, not a single fixed policy.',
                        },
                        {
                            title: 'Retries counted as attempts, not extra tries',
                            body: 'A "3 retries" policy runs a step at most 4 times total (the original attempt plus 3 retries) — an easy off-by-one that\'s handled deliberately.',
                        },
                        {
                            title: 'Resuming is ownership-checked before anything is touched',
                            body: 'A plan can only be resumed by the caller who owns it — checked before the resume writes anything, not after.',
                        },
                    ],
                    whyItMatters:
                        'Long-running, multi-step work is exactly where a naive "just retry the whole thing" approach falls apart — steps that call real external systems (sending something, charging something, provisioning something) can\'t simply be re-run for free. Being precise about exactly what needs to resume, and giving the plan an explicit say in what "this step keeps failing" should mean, is what makes it safe to let an agent run something that takes minutes or hours instead of seconds.',
                },
            },
            {
                id: 'agent-registry',
                name: 'Agent Registry',
                status: 'partial',
                exec: 'A real registry does discover and cache every agent on disk — but it only loads once, at first use, and there\'s no way to add, remove, or refresh an agent without restarting the process.',
                eng: 'IAgentMetadataRegistry scans AGENT.md manifests, caches them for the process lifetime with no invalidation, and supports lookup by id/category/tags; a decorator layers in bundle-scoped agents without touching the base cache.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Deploy time',
                            text: 'A handful of AGENT.md files sit in a few configured folders, each describing one agent.',
                        },
                        {
                            time: 'First request',
                            text: 'The very first time anything actually needs the list of agents, the folders get scanned, each manifest gets parsed, and the result is cached in memory.',
                        },
                        {
                            time: 'Every request after that',
                            text: 'Looking an agent up by id, category, or tag is instant — answered straight from that cache, no disk access involved.',
                        },
                        {
                            time: 'Ten minutes later',
                            text: 'Someone drops a brand-new AGENT.md file into one of those folders, expecting the new agent to show up right away. It doesn\'t — the running process is still serving its original snapshot. Only a restart makes the new agent visible.',
                        },
                    ],
                    flow: [
                        { title: 'Wait For First Ask', tone: 'info', body: 'Nothing gets scanned at startup — the folders are only read the first time something actually asks what agents exist.' },
                        { title: 'Scan & Cache', tone: 'jargon', body: 'Every configured folder is walked, each manifest is parsed, and the whole result is cached in memory for as long as the process runs.' },
                        { title: 'Serve From Memory', tone: 'tip', body: 'Every lookup after that — by id, category, or tag — is answered from the cache, not by touching the filesystem again.' },
                        { title: 'Frozen Until Restart', tone: 'warn', body: 'A manifest added, edited, or removed on disk is invisible to a running process — there\'s no built-in way to make it notice.' },
                    ],
                    narrative: [
                        'A "registry" for agents is meant to be the directory of what exists and how to find it — the thing everything else asks instead of independently poking around the filesystem.',
                        'Correcting the record here: a genuine, dedicated service does this — <mark class="hl">not just a controller</mark> — with its own tests, its own caching behavior, and lookup by id, category, and tags. There\'s even a decorator that layers in agents scoped to an active bundle without polluting the base list. That part is real and already built.',
                        'What keeps this "partial": the registry is <mark class="hl">read-only and loads exactly once</mark>. Once a process starts serving traffic, its view of "what agents exist" is <mark class="hl">frozen until that process restarts</mark> — there\'s no endpoint to refresh it, and no way to register or retire an agent except by changing files on disk and cycling the host. The piece the name implies — a real registry — exists. The piece a "management" claim implies — adding, removing, or refreshing at runtime — doesn\'t yet.',
                    ],
                    techTable: {
                        columns: ['Capability', 'Status'],
                        rows: [
                            ['Discover agents from their manifest files', 'Real'],
                            ['Look up an agent by id, category, or tag', 'Real'],
                            ['Layer in bundle-scoped agents without touching the base list', 'Real'],
                            ['Add or remove an agent while the process is running', 'Not available — requires a restart'],
                            ['Refresh the cache on demand', 'Not available'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Lazy first-load, not startup-load',
                            body: 'Nothing is scanned until the first real call needs it — deliberately mirroring how this codebase\'s skill discovery already works, per its own doc comments.',
                        },
                        {
                            title: 'Bounded, leaf-stopping scan',
                            body: 'Search depth is capped at 3 folder levels, and any folder containing an <code>AGENT.md</code> is treated as a leaf — it isn\'t recursed into further.',
                        },
                        {
                            title: 'A bad manifest degrades quietly',
                            body: 'A malformed <code>AGENT.md</code> is logged and skipped rather than crashing discovery for every other agent.',
                        },
                        {
                            title: 'Bundle agents are layered, not merged in',
                            body: 'The bundle-aware decorator adds scoped agents on top of a lookup; the base registry\'s own cache never stores or sees them.',
                        },
                        {
                            title: 'This entry corrects an earlier, inaccurate description',
                            body: 'An older pass on this page claimed no dedicated registry existed at all. Re-checked directly against the current code for this write-up — it does; the real gap is the lack of runtime refresh.',
                        },
                    ],
                    whyItMatters:
                        '"Knowing what agents exist" sounds trivial until you actually need to add one without downtime, or you change a manifest and can\'t figure out why nothing changed. A real discovery service that\'s frozen until restart is genuinely fine for a small, mostly-static set of agents — but it isn\'t yet the kind of registry an operations team could add or retire agents against live.',
                },
            },
            {
                id: 'eval-store',
                name: 'Eval Store',
                status: 'built',
                exec: 'Every meta-harness evaluation run — candidate skills, benchmark results, what won and why — is recorded, not thrown away after the run.',
                eng: 'AgentEvaluationService + HarnessCandidate persist eval-run results consumed by the meta-harness promotion gate.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A new idea',
                            text: 'The meta-harness proposes a tweaked version of a skill — a candidate — and runs it against a benchmark to see if it\'s actually better.',
                        },
                        {
                            time: 'Results come in',
                            text: 'Not just a single pass/fail number — the outcome of every individual test case in the benchmark is recorded, so you can see exactly where the candidate did better or worse, not just whether it "won" overall.',
                        },
                        {
                            time: 'Accidentally re-submitted',
                            text: 'The same run gets ingested twice by mistake — a retried CI step, a duplicate webhook. Nothing bad happens: the second attempt is recognized as the same run and quietly ignored, rather than creating a confusing duplicate.',
                        },
                        {
                            time: 'Weeks later',
                            text: 'Someone wants to know whether accuracy has actually improved since an older version. Because every run is a permanent record, that comparison is a lookup, not a guess.',
                        },
                    ],
                    flow: [
                        { title: 'Propose', tone: 'info', body: 'A candidate configuration is generated — a specific version of a skill or setup being tried out.' },
                        { title: 'Evaluate', tone: 'jargon', body: 'It runs against the benchmark, and the outcome of every individual case is captured — not just a single headline score.' },
                        { title: 'Record, Once', tone: 'tip', body: 'The full result is written durably under a stable run identifier. Submitting the same run again changes nothing.' },
                        { title: 'Promote Or Fail', tone: 'warn', body: 'The candidate\'s status is updated based on the outcome — a real decision, not just a number sitting in a log somewhere.' },
                    ],
                    narrative: [
                        'The point of an eval store isn\'t just running a benchmark — it\'s making sure the result is <mark class="hl">a permanent record you can come back to</mark>, not a number that flashed by in a console and is gone.',
                        'What\'s real: every run is recorded <mark class="hl">case by case, not just as a summary score</mark>, so you can see exactly which cases got better or worse. Re-submitting the identical run is <mark class="hl">safe by design</mark> — it\'s recognized and ignored rather than duplicated or silently overwritten, because a run\'s record is treated as a fact that doesn\'t change once it\'s written. And each candidate configuration moves through a real lifecycle — proposed, evaluated, and either promoted or marked failed — so the history is a decision trail, not just a pile of scores.',
                        'One honest caveat, straight from the store\'s own documentation: it does <mark class="hl">not yet enforce isolation between tenants</mark> — every ingested run is visible to every caller today. A deployment sharing this across multiple customers needs to add that isolation outside this layer; it\'s a documented gap, not a hidden one.',
                    ],
                    techTable: {
                        columns: ['Guarantee', 'What It Means'],
                        rows: [
                            ['Safe to re-submit', 'Ingesting the same run twice is a no-op — never a duplicate, never a silent overwrite.'],
                            ['Immutable once written', 'A run\'s record is treated as a permanent fact, not something later processes edit.'],
                            ['Safe under concurrent writes', 'One process can record a new run while another is still writing a different one.'],
                            ['No tenant isolation yet', 'Every caller can see every run — real isolation, if needed, has to be added outside this layer.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Idempotency keyed on the run itself',
                            body: 'Re-ingestion is checked against a stable run identifier, not an auto-incrementing row — the same run can be safely re-submitted from a retried pipeline step.',
                        },
                        {
                            title: 'Per-case detail, kept cheap for lists',
                            body: 'Full case-by-case results are retained, while the list view uses a separate lightweight summary so browsing recent runs doesn\'t pay the cost of every case\'s full payload.',
                        },
                        {
                            title: 'A real status lifecycle',
                            body: 'A candidate moves through Proposed, Evaluated, Failed, or Promoted — an explicit decision record, not an implicit one you\'d have to infer from a score.',
                        },
                        {
                            title: 'Score comparisons always use the newest run',
                            body: 'When the same case appears in multiple runs, comparisons resolve to the most recent one — so a historical regression can\'t accidentally get compared against instead of the current picture.',
                        },
                        {
                            title: 'Multi-tenancy is a documented gap, not a silent one',
                            body: 'The store\'s own interface says outright that it surfaces every run to every caller — an honest limitation flagged in the code, matching this page\'s own standard for saying what isn\'t finished.',
                        },
                    ],
                    whyItMatters:
                        'An evaluation system nobody can trust to keep an honest record is worse than none at all — it invites picking whichever run looked best and makes "did we actually get better" impossible to answer months later. Treating every run as a permanent, idempotent, per-case record is what turns a claim like "this candidate won" into something you can actually go back and check.',
                },
            },
        ],
    },
    {
        id: 'observability-evaluation',
        number: '09',
        name: 'Observability & Evaluation',
        subtitle: 'Seeing inside the black box',
        boxes: [
            {
                id: 'distributed-tracing',
                name: 'Distributed Tracing',
                status: 'built',
                exec: 'Lets you replay exactly what an agent did, step by step, down to which specific AI call or tool use caused a failure.',
                eng: 'Nested OpenTelemetry spans (command → turn → tool → LLM call) with GenAI semantic-convention attributes.',
                docLink: '../10-observability.html',
                techs: ['Jaeger', 'Prometheus', 'Azure Monitor'],
                deepDive: {
                    scenario: [
                        {
                            time: 'A complaint comes in',
                            text: 'A user says the agent gave a bad answer somewhere in a long conversation. Nobody knows which step went wrong yet.',
                        },
                        {
                            time: 'Open the trace',
                            text: 'Instead of guessing from logs, someone opens the exact trace for that turn and sees the real sequence: which command ran, which tool got called, which model provider actually answered — and how long each step took.',
                        },
                        {
                            time: 'A wrinkle',
                            text: 'One of the underlying AI libraries involved has no concept of "conversation" or "turn" — on its own, its spans would show up as anonymous fragments with no way to tell which conversation they belong to.',
                        },
                        {
                            time: 'Solved quietly',
                            text: 'A separate mechanism tags every one of those anonymous spans with the right conversation and turn after the fact, so the whole trace still reads as one coherent story instead of disconnected pieces from different libraries.',
                        },
                    ],
                    flow: [
                        { title: 'Set The Context', tone: 'info', body: 'At the start of a turn, the conversation and turn identity is attached so everything that happens next can be tied back to it.' },
                        { title: 'Nest The Work', tone: 'jargon', body: 'Each layer opens its own span inside the one before it — command, then turn, then a tool call or a model call.' },
                        { title: 'Tag What The Libraries Don\'t Know', tone: 'warn', body: 'Third-party AI libraries emit their own spans with no idea what conversation or turn they belong to — those get tagged afterward.' },
                        { title: 'Ship To A Real Backend', tone: 'tip', body: 'The finished trace goes to standard tracing tools, not a bespoke log format only this codebase can read.' },
                    ],
                    narrative: [
                        'Distributed tracing means being able to <mark class="hl">literally replay, step by step, what happened during one specific turn</mark> — not just "an error occurred somewhere."',
                        'What\'s real: this uses the actual <mark class="hl">industry-standard vocabulary for describing AI operations</mark> (OpenTelemetry\'s GenAI conventions), not a made-up schema — so it works with tracing tools built for that standard rather than something bespoke. It also solves a genuinely non-obvious problem: several different third-party AI libraries are stitched together here, and <mark class="hl">none of them know what a "conversation" or "turn" is</mark> on their own. A dedicated mechanism tags every one of their spans with that context after the fact, so what would otherwise be disconnected fragments from different libraries reads as one coherent trace.',
                        'Where it goes a step further: there\'s a harness-specific extension to that standard vocabulary for things the official spec doesn\'t cover yet, and it\'s <mark class="hl">deliberately kept in its own separate namespace</mark> specifically so it won\'t collide with whatever the official spec adds later.',
                    ],
                    techTable: {
                        columns: ['Span Layer', 'What It Represents'],
                        rows: [
                            ['Command', 'The top-level request that triggered a turn.'],
                            ['Turn', 'One exchange within a conversation.'],
                            ['Tool call', 'A single tool the agent invoked during that turn.'],
                            ['Model call', 'A single request sent to an LLM provider.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Standard vocabulary, not a bespoke schema',
                            body: 'Traces use OpenTelemetry\'s official GenAI semantic conventions, so they work with tracing tools built for that spec rather than something proprietary.',
                        },
                        {
                            title: 'Harness extensions are namespaced on purpose',
                            body: 'Anything the official spec doesn\'t cover yet lives under its own prefix specifically so it can\'t collide with a future official addition.',
                        },
                        {
                            title: 'Third-party library spans get tagged after the fact',
                            body: 'A dedicated processor copies conversation, turn, and agent identity onto spans emitted by AI libraries that have no concept of any of the three themselves.',
                        },
                        {
                            title: 'Tracks intended provider vs. actual provider',
                            body: 'A span records which model provider a call was originally meant for, separately from which one it actually ran on after a fallback — useful for diagnosing failover behavior after the fact.',
                        },
                        {
                            title: 'Already wired to multiple real backends',
                            body: 'Traces and metrics already ship to more than one real tool (Jaeger, Prometheus, Azure Monitor) rather than being locked to a single vendor.',
                        },
                    ],
                    whyItMatters:
                        'An agent that calls tools, hands off between skills, and falls back across model providers is genuinely hard to debug from plain logs — too many moving pieces, too many places something could have gone wrong. Being able to open one trace and see the actual sequence of what happened, not a guess reconstructed after the fact, is the difference between debugging with evidence and debugging by hunch.',
                },
            },
            {
                id: 'metrics',
                name: 'Metrics',
                status: 'built',
                exec: 'Goes well past the ordinary operational basics — tracks AI-specific signals a normal web service dashboard has no concept of, like real dollar cost per session and whether a called tool actually helped.',
                eng: 'Nearly 20 dedicated OpenTelemetry metric groups (session cost, resilience, content safety, tool usefulness, drift, and more), exported to Prometheus/Grafana or Azure Monitor.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A cost question',
                            text: 'A team wants to know if a recent change made the agent more expensive to run. Instead of waiting for a cloud bill weeks later, they check per-session cost — tracked as a real dollar figure, updated as sessions happen.',
                        },
                        {
                            time: 'A quality question',
                            text: 'Someone wonders whether a particular tool is actually helping, or if the agent keeps calling it and getting nothing useful back. There\'s a metric specifically for that — not just "was the tool called," but whether its result was actually used for anything.',
                        },
                        {
                            time: 'A reliability question',
                            text: 'A model provider starts failing intermittently. A counter tracking fallback activations spikes immediately on a dashboard, well before anyone would have noticed from user complaints.',
                        },
                    ],
                    flow: [
                        { title: 'Instrument At The Source', tone: 'info', body: 'A counter or duration is recorded right where the real event happens — a tool call, a safety block, a fallback — not reconstructed afterward from logs.' },
                        { title: 'Export Through One Pipeline', tone: 'jargon', body: 'Metrics travel through the same OpenTelemetry pipeline as traces, so the two can be correlated together instead of living in separate systems.' },
                        { title: 'Land In A Real Dashboard', tone: 'tip', body: 'The same instrumentation flows to Prometheus/Grafana locally or Azure Monitor in the cloud, without changing any code.' },
                    ],
                    narrative: [
                        'The usual meaning of "metrics" for a web service is latency, error rate, and throughput — the operational basics, and those are genuinely here too.',
                        'What\'s actually built goes well past that: this system tracks <mark class="hl">signals specific to running AI at scale</mark> that a typical service dashboard has no concept of — the real dollar cost of a session, whether a tool the agent called was genuinely useful or just noise, how often a request had to fall back to a backup model provider, and how much content got blocked or redacted for safety.',
                        'The scope is real, not a token gesture: <mark class="hl">nearly twenty separate, purpose-built metric groups</mark> exist, covering cost, safety, resilience, tool quality, and governance signals alongside the ordinary operational ones — not one generic counter bolted onto an otherwise plain APM setup.',
                    ],
                    techTable: {
                        columns: ['Metric Group', 'What It Tracks'],
                        rows: [
                            ['Session cost', 'The real dollar cost of a session — not just a token count someone has to convert later.'],
                            ['Resilience', 'Provider fallback activations, circuit breaker transitions, retry attempts per provider.'],
                            ['Content safety', 'Evaluations run, content blocked, PII redactions performed.'],
                            ['Tool usefulness', 'Whether a called tool\'s result was actually useful, not just whether it ran.'],
                            ['Drift', 'Quality signals moving away from baseline over time (see Drift Detection).'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Nearly 20 dedicated metric groups',
                            body: 'Separate, purpose-built groups exist for cost, safety, resilience, tool quality, governance, and the AI-usage basics — not a single catch-all counter file.',
                        },
                        {
                            title: 'Cost is tracked in real dollars',
                            body: 'Session cost is recorded as an actual USD figure at the point of measurement, not left as a token count for someone else to price out later.',
                        },
                        {
                            title: 'Tool usefulness is measured, not just tool usage',
                            body: 'A dedicated metric distinguishes a tool that ran from a tool whose result actually mattered — a meaningfully harder thing to track than a simple call counter.',
                        },
                        {
                            title: 'Metrics and traces share one pipeline',
                            body: 'Both travel through the same OpenTelemetry instrumentation, so a spike in a metric and the trace that explains it live in the same system rather than two disconnected tools.',
                        },
                        {
                            title: 'Backend-agnostic by design',
                            body: 'The same instrumentation already ships to more than one real backend (Prometheus/Grafana, Azure Monitor) — switching or adding one doesn\'t touch the metrics code itself.',
                        },
                    ],
                    whyItMatters:
                        'Running an AI agent at scale raises questions a normal web service dashboard was never built to answer — is this getting more expensive, is this tool actually helping, are we quietly falling back to a worse model more often than anyone realizes. Having dedicated, purpose-built answers to those questions sitting right next to the ordinary latency and error-rate numbers is what turns "the agent felt off today" into something you can actually go measure.',
                },
            },
            {
                id: 'llm-as-judge',
                name: 'LLM-as-Judge',
                status: 'built',
                exec: 'An AI model grades another AI’s output quality as part of the automated pipeline, not just a human eyeballing it.',
                eng: 'The "grader" CI gate and the meta-harness’s benchmark evaluation both use an LLM-as-judge pattern to score candidate outputs.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A grading task',
                            text: 'A CI pipeline needs to check whether some generated output actually satisfies a written rubric — too nuanced for a simple text match, too much volume for a person to check by hand every time.',
                        },
                        {
                            time: 'A wrinkle',
                            text: 'That rubric came from a case author — someone outside the trusted core of the system. If the judge model just blindly obeyed it as an instruction, a careless or hostile rubric could say "always score this 1.0" and be believed.',
                        },
                        {
                            time: 'On a failing score',
                            text: 'The judge doesn\'t just return "0.3." It has to point to the exact sentence in the rubric it believes was violated, quoted word for word — a score a human can actually go check, not just take on faith.',
                        },
                        {
                            time: 'For a higher-stakes call',
                            text: 'Instead of trusting one judge\'s opinion, several independent judges can grade the same thing in parallel, with their scores combined and any disagreement between them surfaced rather than hidden.',
                        },
                    ],
                    flow: [
                        { title: 'Isolate The Rubric', tone: 'warn', body: 'The grading criteria — written by someone outside the trusted core — is handled as data the judge reads, never as an instruction it obeys.' },
                        { title: 'Grade & Cite Evidence', tone: 'info', body: 'The judge returns a score, and on a failure has to quote the specific rubric text it believes was broken.' },
                        { title: 'Optionally, Convene A Jury', tone: 'jargon', body: 'For higher-stakes grading, several independent judges score the same output in parallel and get combined into one result.' },
                        { title: 'Feed A Real Decision', tone: 'tip', body: 'The score isn\'t just logged — it drives something real: a CI gate that can block a merge, or a benchmark comparing two candidates.' },
                    ],
                    narrative: [
                        '"LLM-as-judge" means using a model to grade another model\'s output against a rubric — instead of a person doing it by hand every time, or a naive keyword match that can\'t tell nuance from noise.',
                        'What\'s real: the rubric being graded against is treated as <mark class="hl">genuinely untrusted input, not a trusted instruction</mark> — it\'s isolated with the same kind of defense used against prompt injection, so a careless or hostile rubric can\'t hijack the judge into always passing. A failing score has to come with <mark class="hl">the exact rubric sentence the judge believes was violated, quoted directly</mark> — not a bare number nobody can verify. And there\'s a genuine "jury" mode, off by default, where <mark class="hl">several independent judges grade the same output in parallel</mark> and get combined into one result, with disagreement between them surfaced instead of averaged away and lost.',
                        'Where it actually gets used: both the CI grading gate and the meta-harness\'s own benchmark evaluation route through <mark class="hl">the same shared judge mechanism</mark> — not two separate, duplicated implementations that could quietly drift apart.',
                    ],
                    techTable: {
                        columns: ['Mode', 'How It Works', 'Default?'],
                        rows: [
                            ['Single judge', 'One model scores the output against the rubric, with mandatory quoted evidence on a failing score.', 'Yes'],
                            ['Jury (panel)', 'Several independent judges score the same output in parallel; results are combined and disagreement is surfaced.', 'No — off by default'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'The rubric is data, not an instruction',
                            body: 'It\'s placed in the untrusted-input part of the prompt specifically so it can\'t say "score everything 1.0" and have the judge obey it as a system-level command.',
                        },
                        {
                            title: 'A failing score requires cited evidence',
                            body: 'The judge must quote the exact rubric sentence it believes was violated — a score you can check, not just a number you have to trust.',
                        },
                        {
                            title: 'Jury mode costs nothing when unused',
                            body: 'With no panel configured, it delegates straight through to the single-judge path with zero extra model calls — not a parallel system that has to be kept in sync.',
                        },
                        {
                            title: 'A bad panelist doesn\'t spoil the result',
                            body: 'If one judge in a panel errors out or returns garbage, it\'s excluded from the aggregate rather than failing the whole evaluation — though its cost still counts.',
                        },
                        {
                            title: 'One mechanism, two real consumers',
                            body: 'The CI grading gate and the meta-harness\'s benchmark evaluation both route through the same judge infrastructure, rather than each maintaining its own copy.',
                        },
                    ],
                    whyItMatters:
                        'Using an AI to grade AI output can sound like it\'s just vibes wearing a lab coat. The difference between that and something you can actually trust inside a CI gate is exactly these details: treating the grading criteria as something that could be attacked, forcing the judge to show its work instead of handing back a bare number, and having a way to check one judge\'s opinion against several when the decision actually matters.',
                },
            },
            {
                id: 'human-review',
                name: 'Human Review',
                status: 'built',
                exec: 'A person can be pulled into the loop to review or approve something before it goes further.',
                eng: 'HumanFeedbackRelay carries a pending decision to a human reviewer and the response back into the run.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A risky step',
                            text: 'The agent is about to do something that warrants a second opinion. It pauses and hands the decision to a person instead of just proceeding.',
                        },
                        {
                            time: 'The reviewer responds',
                            text: 'A human writes free-text feedback — "not yet, check with legal first" — and that response needs to go back to the agent so it can actually act on it.',
                        },
                        {
                            time: 'The uncomfortable question',
                            text: 'That reviewer\'s own words are about to be fed back into the model as part of its next instructions. What if the reviewer\'s account is compromised, or their comment happens to contain something that reads like a command?',
                        },
                        {
                            time: 'How it\'s handled',
                            text: 'The reviewer\'s text is wrapped in a marker the reviewer could never have predicted or forged, explicitly labeled "not a system instruction," and attributed by name — so the model can weigh it as input from a person, never mistake it for a command from itself or its operators.',
                        },
                    ],
                    flow: [
                        { title: 'Pause For A Person', tone: 'info', body: 'A step that warrants it hands off to a human reviewer instead of the agent deciding alone.' },
                        { title: 'Collect Free-Text Feedback', tone: 'jargon', body: 'The reviewer can write actual instructions back, not just approve or deny.' },
                        { title: 'Wrap It Unforgeably', tone: 'warn', body: 'That feedback is sealed with a random, one-time marker before it\'s relayed to the model, so nothing else — including the reviewer\'s own text — can impersonate it.' },
                        { title: 'Resume, Informed', tone: 'tip', body: 'The agent continues with the human\'s actual words available to it, clearly attributed and clearly not a command.' },
                    ],
                    narrative: [
                        'Human review means being able to <mark class="hl">genuinely pause an automated process and wait for a person</mark> — not a UI element that logs "reviewed" without actually blocking anything.',
                        'What\'s real: a reviewer\'s free-text response is carried all the way back into the running agent, not just recorded as an approve/deny checkbox. The interesting engineering is in how that gets done safely — the reviewer\'s own words are wrapped in <mark class="hl">a random, one-time marker minted fresh for that message</mark>, one the reviewer has no way to predict or copy, and explicitly labeled as feedback, never a system instruction. That closes a real gap: earlier designs used a fixed, published delimiter, which meant anyone who read this code\'s own source knew exactly what string to forge inside their own feedback to make it look more authoritative than it is. For decisions that need more than one person, there\'s also a real <mark class="hl">multi-approver mechanism</mark> — require just one of several people to sign off, require all of them, or require a specific number out of a larger group.',
                        'This is marked <mark class="hl">"Built," not "Partial"</mark> — the pause-and-wait mechanism, the unforgeable feedback wrapping, and all three multi-approver modes are real and exercised, not a UI mockup of a review step.',
                    ],
                    techTable: {
                        columns: ['Approval Mode', 'What It Requires'],
                        rows: [
                            ['Any of', 'The first reviewer to respond decides — fastest resolution.'],
                            ['All of', 'Every designated reviewer must approve; a single denial ends it immediately.'],
                            ['Quorum', 'A specific number out of a larger group of reviewers must agree.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Unforgeable by construction, not by escaping',
                            body: 'Each relayed message gets a fresh random tag rather than a fixed marker — there\'s no published constant left for a reviewer\'s own text to copy or impersonate.',
                        },
                        {
                            title: 'The attribution line is sanitized too',
                            body: 'A reviewer\'s own name or identity string is stripped of characters that could let it break out of its line and forge the start of fake content.',
                        },
                        {
                            title: 'Explicitly labeled as input, not command',
                            body: 'The wrapped text tells the model outright to weigh it "like any other input" — the framing itself pushes back on treating a human comment as an override.',
                        },
                        {
                            title: 'Sanitization happens before wrapping, not instead of it',
                            body: 'This mechanism only handles attribution and delimiting — the actual PII/injection scrubbing of the reviewer\'s text is a separate, required step that has to run first.',
                        },
                        {
                            title: 'Three real approval strategies, not one implied by a checkbox',
                            body: 'Any-of, all-of, and quorum-based approval are all implemented as distinct strategies a request can be configured to use.',
                        },
                    ],
                    whyItMatters:
                        'Putting a human in the loop only matters if the human\'s actual judgment reaches the agent intact — and if a person\'s own words can\'t be twisted into looking like a system command, by an attacker or by accident. Getting both of those right is what makes "a person can step in" a real safety property instead of a comforting UI label.',
                },
            },
            {
                id: 'drift-detection',
                name: 'Drift Detection',
                status: 'built',
                exec: 'The system watches for its own behavior quietly changing over time and flags it, instead of finding out from a user complaint.',
                eng: 'EWMA (exponentially weighted moving average) drift detection over governance/quality signals.',
                deepDive: {
                    scenario: [
                        {
                            time: 'Nothing obvious happens',
                            text: 'No one changed anything on purpose, but a model provider update or a subtle prompt tweak makes the agent\'s answers a little less faithful to its source material over the following week.',
                        },
                        {
                            time: 'No complaints yet',
                            text: 'The change is too gradual for any single conversation to look obviously wrong. Nobody notices, because nobody\'s looking at any one interaction long enough to see a slow slide.',
                        },
                        {
                            time: 'The system notices anyway',
                            text: 'One specific quality signal — faithfulness to source material, tracked separately from everything else — has been quietly sliding for days compared to what\'s normal for that particular kind of task.',
                        },
                        {
                            time: 'It escalates',
                            text: 'Past a defined threshold, this gets flagged for a person\'s attention automatically — instead of everyone finding out three weeks later from a pattern of complaints.',
                        },
                    ],
                    flow: [
                        { title: 'Score Several Dimensions Separately', tone: 'info', body: 'Quality isn\'t one blended number — faithfulness, relevance, structure, tool accuracy, coherence, and instruction-following are each tracked on their own.' },
                        { title: 'Compare Against A Moving Baseline', tone: 'jargon', body: 'Each new score is weighed against what\'s normal, weighted so recent behavior counts for more than old behavior.' },
                        { title: 'Classify The Severity', tone: 'warn', body: 'The result lands in one of four tiers — none, warn, alert, or escalate — each triggering a different level of response.' },
                        { title: 'Escalate, Don\'t Just Log', tone: 'tip', body: 'A genuinely serious drift pulls in the same human-escalation mechanism used elsewhere in the system, not just a line in a log nobody reads.' },
                    ],
                    narrative: [
                        'The point of drift detection is catching quality <mark class="hl">quietly getting worse over time</mark>, before a user\'s complaint becomes the alarm.',
                        'What\'s real: quality isn\'t reduced to one number. <mark class="hl">Six independent dimensions are tracked separately</mark> — faithfulness, relevance, structural conformance, tool usage accuracy, coherence, and instruction following — so a regression in one specific area shows up on its own instead of getting averaged away into a score that still looks fine overall. Each one is compared against a <mark class="hl">moving baseline that weights recent behavior more heavily than old behavior</mark>, and that baseline has a real fallback: if there isn\'t enough history yet for a narrow, specific task, the comparison automatically <mark class="hl">widens to the whole skill, then the whole agent</mark>, instead of having nothing meaningful to compare against.',
                        'The severity model is real, not cosmetic: <mark class="hl">four distinct tiers</mark>, and only the most serious one actually pulls a human into the loop through the same escalation mechanism the rest of the system uses — the lower tiers log and notify without waking anyone up unnecessarily.',
                    ],
                    techTable: {
                        columns: ['Dimension', 'What It Catches'],
                        rows: [
                            ['Faithfulness', 'Output drifting away from what the source material actually says.'],
                            ['Relevance', 'Answers that are technically fine but stop actually addressing what was asked.'],
                            ['Structural conformance', 'Output quietly stops following its expected format or schema.'],
                            ['Tool usage accuracy', 'Tools invoked with increasingly wrong or malformed arguments.'],
                            ['Coherence', 'Logical flow within an answer breaking down.'],
                            ['Instruction following', 'The agent drifting away from its own system prompt or skill instructions.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Six dimensions, tracked independently',
                            body: 'A regression in one specific area is visible on its own — it can\'t hide behind an average that still looks acceptable.',
                        },
                        {
                            title: 'A real baseline fallback chain',
                            body: 'Comparisons scope to task type first, then widen to skill, then to the whole agent — so a narrow, low-traffic task still has something meaningful to be measured against.',
                        },
                        {
                            title: 'Same weighting philosophy used elsewhere in this harness',
                            body: 'Recent behavior counts more than old behavior via a weighted moving average — the same underlying idea as the decay curve in Episodic Memory, applied here to quality instead of relevance.',
                        },
                        {
                            title: 'Four severity tiers with genuinely different consequences',
                            body: 'Warn and Alert log and notify; only Escalate triggers the shared human-escalation flow — the tiers aren\'t cosmetic labels on the same behavior.',
                        },
                        {
                            title: 'Every evaluation is recorded, not just the alarming ones',
                            body: 'Results are written to an audit store and persisted into the knowledge graph, so "why did this get flagged" has a real trail to check, not just a final verdict.',
                        },
                    ],
                    whyItMatters:
                        'The failure mode this exists to prevent is quiet, cumulative decay nobody notices until real damage is already done — an agent that\'s subtly gotten worse while still technically "working" on every individual interaction. Watching several distinct quality signals separately, weighed against what\'s actually normal for that specific task, is what catches "getting a little worse every day" instead of only catching outright broken.',
                },
            },
            {
                id: 'learnings-log',
                name: 'Learnings Log',
                status: 'built',
                exec: 'Insights the meta-harness discovers get written down somewhere durable, not lost at the end of the run that found them.',
                eng: 'The meta-harness learnings memory channel, gated the same way other cross-run memory is.',
                deepDive: {
                    scenario: [
                        {
                            time: 'A run finds something',
                            text: 'An optimization run discovers a genuine, recurring mistake pattern. Instead of that insight living only in that run\'s own disposable log, it gets written down as a durable "learning."',
                        },
                        {
                            time: 'Weeks later',
                            text: 'A completely different optimization run, working on a related problem, recalls that exact learning instead of rediscovering the same mistake from zero.',
                        },
                        {
                            time: 'Meanwhile, in production',
                            text: 'Drift detection independently notices the same category of quality regression and generates its own corrective learning automatically — tagged with exactly where it came from, joining the same pool a human correction would.',
                        },
                        {
                            time: 'An attempted manipulation',
                            text: 'Someone tries to slip a manipulative instruction into what looks like a legitimate learning entry. The same safety gate that screens memories elsewhere in the harness catches it here too — and the caller only ever sees a generic "rejected," never the specific reason that would help refine the attempt.',
                        },
                    ],
                    flow: [
                        { title: 'Something Worth Keeping Happens', tone: 'info', body: 'A correction, a drift alert, a resolved escalation, or the agent catching its own mistake.' },
                        { title: 'Screen It', tone: 'warn', body: 'The same safety gate used elsewhere in the harness checks it before it\'s trusted — a learning gets no special pass.' },
                        { title: 'Record It, With Its Origin', tone: 'jargon', body: 'The learning is saved along with exactly where it came from, not anonymously.' },
                        { title: 'Recall It Later', tone: 'tip', body: 'A future run — potentially unrelated to the one that discovered it — can pull it back up instead of relearning the same lesson from scratch.' },
                    ],
                    narrative: [
                        'The point of a learnings log is that <mark class="hl">a run that discovers something valuable shouldn\'t lose it the moment the run ends</mark> — the insight should be available to every future run, not locked inside one disposable log.',
                        'What\'s real: this goes through the <mark class="hl">exact same Remember/Recall/Forget/Improve operations</mark> as the rest of this harness\'s memory system, and through <mark class="hl">the identical safety gate</mark> that screens every other memory write. A manipulative "learning" gets rejected the same way a manipulative memory would — and the caller only ever sees a scrubbed, generic rejection, never the gate\'s actual classification reason, specifically so <mark class="hl">probing it for weaknesses teaches an attacker nothing useful</mark>. It\'s also genuinely wired into the rest of the system: drift detection can generate its own corrective learnings automatically when it finds a regression, and every learning is tagged with real provenance — a human correction, a drift-detection finding, a resolved escalation, the agent\'s own self-correction, or a manual entry.',
                        'Because it rides the standard command pipeline like everything else in this harness, each write also gets the same <mark class="hl">validation, audit trail, and telemetry</mark> any other operation gets — not a special-cased shortcut bolted on separately.',
                    ],
                    techTable: {
                        columns: ['Origin', 'What Produced It'],
                        rows: [
                            ['Human correction', 'A person explicitly corrected the agent\'s output.'],
                            ['Drift detection', 'An automatically-detected quality regression generated its own corrective entry.'],
                            ['Escalation resolution', 'A human-reviewed escalation was resolved with a correction.'],
                            ['Agent self-improvement', 'The agent caught and corrected its own mistake.'],
                            ['Manual entry', 'An operator entered it directly.'],
                        ],
                    },
                    engineeringFacts: [
                        {
                            title: 'Same four operations as the rest of this harness\'s memory',
                            body: 'Remember, Recall, Forget, and Improve — not a separate, one-off mechanism built just for learnings.',
                        },
                        {
                            title: 'Screened by the same safety gate as any other memory write',
                            body: 'A learning gets no special exemption from the injection-screening gate that protects the rest of the harness\'s memory.',
                        },
                        {
                            title: 'Rejection reasons are deliberately hidden from the caller',
                            body: 'A refused write returns a stable, scrubbed error code — the gate\'s actual classification stays internal so probing it doesn\'t teach anything useful.',
                        },
                        {
                            title: 'Real provenance, not an anonymous pool',
                            body: 'Five distinct source types are tracked — used both for audit reporting and by drift detection\'s own filtering logic.',
                        },
                        {
                            title: 'Routed through the standard command pipeline',
                            body: 'Every write goes through the same validation, audit, and telemetry pipeline as any other command in this system, rather than a special-cased shortcut.',
                        },
                    ],
                    whyItMatters:
                        'An optimization process that forgets everything the moment each run ends is doomed to relearn the same lessons forever. Treating a discovered correction as a durable, safety-screened, provenance-tagged memory — not just a line in that run\'s own disposable log — is what lets improvement actually compound across runs instead of restarting from zero every time.',
                },
            },
        ],
    },
    {
        id: 'governance-control',
        number: '10',
        name: 'Governance & Control',
        subtitle: 'Keeping agents safe and accountable',
        boxes: [
            {
                id: 'runtime-guardrails',
                name: 'Runtime Guardrails',
                status: 'built',
                exec: 'Every tool call, no matter where the tool came from, passes through the same three safety checks before anything actually happens.',
                eng: 'GovernedAIFunction.InvokeCoreAsync runs IToolInvocationGovernor → IToolClassificationGate → IProgressEvaluator, in order.',
            },
            {
                id: 'audit-log',
                name: 'Audit Log',
                status: 'built',
                exec: 'A record of what happened that can’t be quietly edited after the fact.',
                eng: 'A durable PostgreSQL audit store backed by a hash-chained JSONL log, where each entry’s hash includes the previous entry’s.',
            },
            {
                id: 'human-escalation',
                name: 'Human Escalation',
                status: 'built',
                exec: 'Risky actions can require a human’s sign-off before they happen, with the caution level matched to how much damage the action could do.',
                eng: 'A tool’s RiskTier (BlastRadius) feeds the permission resolver’s autonomy-tier gate; a PendingApproval decision blocks fail-closed.',
            },
            {
                id: 'autonomy-tiers',
                name: 'Autonomy Tiers',
                status: 'built',
                exec: 'How independently an agent is allowed to act is a graded, configurable setting — not all-or-nothing.',
                eng: 'AutonomyLevel on plugin/tool declarations, resolved alongside RiskTier at invocation time.',
            },
            {
                id: 'compliance-reporting',
                name: 'Compliance Reporting',
                status: 'not-built',
                exec: 'Not built as a distinct feature. The audit log and safety gates exist; a formal compliance-report generator on top of them doesn’t.',
            },
        ],
    },
    {
        id: 'system-of-record',
        number: '11',
        name: 'System of Record',
        subtitle: 'The enterprise truth the platform serves — not something this harness builds',
        boxes: [
            {
                id: 'crm-customer-data',
                name: 'CRM & Customer Data',
                status: 'external',
                exec: 'An outside system this harness would connect to through its RAG/tool/MCP layers — not something built here, deliberately.',
            },
            {
                id: 'erp-operations',
                name: 'ERP & Operations',
                status: 'external',
                exec: 'Same story — a real enterprise system this harness integrates with, never replaces.',
            },
            {
                id: 'identity-hr-systems',
                name: 'Identity & HR Systems',
                status: 'external',
                exec: 'This harness authenticates against your identity provider (see Identity & Trust); it doesn’t become one.',
            },
            {
                id: 'service-management',
                name: 'Service Management',
                status: 'external',
                exec: 'An external ticketing/ITSM system a tool or MCP connector could reach — not built in this repo.',
            },
            {
                id: 'data-platform',
                name: 'Data Platform',
                status: 'external',
                exec: 'Your actual enterprise data warehouse/lake — the RAG pipeline retrieves from sources like this, it doesn’t replace them.',
            },
            {
                id: 'master-data-management',
                name: 'Master Data Management',
                status: 'external',
                exec: 'The authoritative system of record for entities this harness’s knowledge graph might reference — external by design.',
            },
        ],
    },
];

/**
 * Technology comparison entries referenced by any box's `techs` array above.
 * Shape: { blurb, pros: string[], cons: string[], best, maturity }.
 */
window.SHOWCASE_TECH_CATALOG = {
    'Dense + BM25 Hybrid (default)': {
        blurb: "The harness's own default: combines vector similarity search with classic BM25 keyword search, merged via Reciprocal Rank Fusion.",
        pros: [
            'Works out of the box, no extra service to deploy',
            'Catches both paraphrased questions and exact keyword/product-code matches',
            "Runs entirely inside the harness's own process",
        ],
        cons: [
            'Retrieval quality scales with how much tuning goes into chunking and embeddings',
            'No managed relevance-tuning UI — every knob is code',
        ],
        best: 'Teams who want strong retrieval without standing up or paying for a separate search service.',
        maturity: 'Production (default path)',
    },
    'Azure AI Search (agentic retrieval)': {
        blurb: "Microsoft's managed hybrid search service, with an agentic retrieval mode that plans multi-step queries itself.",
        pros: [
            'Fully managed — no infrastructure to run',
            'Built-in agentic query planning reduces manual query engineering',
            'Deep Azure ecosystem integration (identity, networking, monitoring)',
        ],
        cons: [
            'Recurring Azure cost that scales with index size and query volume',
            'Ties the harness to Azure specifically',
            'Less control over the exact ranking algorithm than the in-process default',
        ],
        best: 'Azure-committed teams who want a managed search backend and are fine trading control for less operational burden.',
        maturity: 'GA',
    },
    FAISS: {
        blurb: "Meta's open-source library for fast approximate nearest-neighbor vector search, run in-process.",
        pros: [
            'Extremely fast at large vector counts',
            'No external service, no network hop',
            'Free and widely used in production ML systems',
        ],
        cons: [
            'Vector-only — hybrid search needs a keyword component hand-assembled alongside it',
            'No built-in persistence layer — the indexing lifecycle is your own responsibility',
            'No access control or multi-tenancy built in',
        ],
        best: "High-volume, latency-sensitive retrieval where you're comfortable owning the index lifecycle yourself.",
        maturity: 'Mature / widely adopted',
    },
    'SQLite FTS5': {
        blurb: "SQLite's built-in full-text search extension — keyword search with zero extra infrastructure.",
        pros: [
            'Ships inside a single file, nothing else to deploy or operate',
            'Great fit for local dev, small deployments, or embedded scenarios',
            'Simple, well-understood query syntax',
        ],
        cons: [
            "Keyword-only — no semantic/vector matching on its own",
            "Doesn't scale to the concurrency or index sizes a dedicated search service handles",
            'No ranking sophistication beyond BM25',
        ],
        best: "Local development, small single-tenant deployments, or anywhere a separate search service isn't worth the operational cost.",
        maturity: 'Mature (part of SQLite core)',
    },
    Neo4j: {
        blurb: 'The most widely-used dedicated graph database, with its own query language (Cypher) and a large ecosystem of graph algorithms.',
        pros: [
            'Mature tooling, visualization, and a large community',
            'Rich built-in graph algorithm library, including community detection',
            'Battle-tested at production scale',
        ],
        cons: [
            'Commercial licensing for clustering/enterprise features',
            'Another stateful service to operate and back up',
            'Steeper learning curve (Cypher) for a team new to graph databases',
        ],
        best: 'Teams that expect the knowledge graph to grow large and want a dedicated, well-supported graph engine.',
        maturity: 'Production',
    },
    Kuzu: {
        blurb: 'An embedded, open-source graph database — runs in-process like SQLite, but for graphs.',
        pros: [
            'No separate service to deploy or operate',
            'Free and fully open-source',
            'Fast for single-node workloads',
        ],
        cons: [
            'Younger project — smaller ecosystem and community than Neo4j',
            'Not designed for multi-node horizontal scale',
            'Fewer built-in graph algorithms out of the box',
        ],
        best: 'Teams that want real graph-query capability without taking on a new operated service.',
        maturity: 'Active / early production',
    },
    PostgreSQL: {
        blurb: 'Using Postgres itself as the graph store, reusing infrastructure the team already runs.',
        pros: [
            'No new database technology to introduce — most teams already run Postgres',
            'One less service to secure, back up, and monitor',
            "Transactional guarantees the harness's other relational data already relies on",
        ],
        cons: [
            'Graph traversal queries are less natural and often slower than a purpose-built graph engine at scale',
            'No dedicated graph algorithm library — community detection etc. has to be implemented or bolted on',
        ],
        best: 'Teams that already run Postgres and want to avoid adding a new database technology purely for the knowledge graph.',
        maturity: 'Production (via existing Postgres infrastructure)',
    },
    'In-memory (dev/test)': {
        blurb: 'A non-persistent, in-process graph store used for local development and automated tests.',
        pros: [
            'Zero setup — nothing to install or configure',
            'Fast test runs with no external dependency',
            'Identical interface to the production backends, so tests exercise real code paths',
        ],
        cons: [
            "Data doesn't survive a process restart",
            'Not viable for any real deployment',
            'Not representative of production performance characteristics',
        ],
        best: 'Local development and CI test runs only.',
        maturity: 'Dev/test only — not for production',
    },
    Jaeger: {
        blurb: 'Open-source distributed tracing backend — stores and visualizes the spans OpenTelemetry emits.',
        pros: [
            'Free, open-source, self-hostable',
            'Purpose-built trace visualization (waterfall views, span search)',
            'No vendor lock-in',
        ],
        cons: [
            'You own its operation, storage, and retention policy',
            'No built-in alerting — typically paired with Prometheus/Grafana for that',
        ],
        best: 'Teams that want full control over their tracing backend and are comfortable self-hosting.',
        maturity: 'Production / CNCF graduated project',
    },
    Prometheus: {
        blurb: 'The standard open-source metrics store and query engine (PromQL), typically paired with Grafana for dashboards.',
        pros: [
            'Industry-standard, huge ecosystem of exporters and dashboards',
            'Powerful query language for aggregating metrics over time',
            'Free and self-hostable',
        ],
        cons: [
            'Metrics only — not a substitute for trace or log storage',
            'Long-term retention at scale needs an additional remote-storage backend',
        ],
        best: 'Teams that want standard, dashboard-ready operational metrics alongside tracing.',
        maturity: 'Production / CNCF graduated project',
    },
    'Azure Monitor': {
        blurb: "Microsoft's managed observability platform — collects traces, metrics, and logs into one Azure-native service.",
        pros: [
            'Fully managed — no tracing/metrics infrastructure to run',
            'Single pane of glass alongside other Azure resource monitoring',
            'Built-in alerting and retention without extra setup',
        ],
        cons: [
            'Recurring Azure cost that scales with telemetry volume',
            'Ties observability tooling to Azure specifically',
        ],
        best: 'Azure-hosted deployments that want managed observability without operating Jaeger/Prometheus themselves.',
        maturity: 'GA',
    },
    'HTTP Transport': {
        blurb: "MCP over standard HTTP — the harness's tools are reachable as a normal web endpoint.",
        pros: [
            'Works across process and network boundaries, including to/from other machines',
            'Standard auth patterns apply (JWT bearer, API gateways, load balancers)',
            'Easiest to expose to external MCP clients',
        ],
        cons: [
            "Network latency and failure modes a local transport doesn't have",
            "Needs real authentication/authorization since it's reachable off-box",
        ],
        best: "Exposing this harness's tools to other systems, or consuming tools hosted elsewhere.",
        maturity: 'Production',
    },
    'stdio Transport': {
        blurb: 'MCP over standard input/output — the tool server runs as a local child process with no network involved.',
        pros: [
            'No network stack, no ports to secure — the tightest possible trust boundary',
            'Simplest possible setup for a tool that only ever runs alongside the agent',
            'Lower latency than a network hop',
        ],
        cons: [
            'Only works for same-machine, same-process-tree scenarios',
            'No support for multiple concurrent remote clients',
        ],
        best: 'Tools that only ever need to run alongside the agent process itself, with no remote-access requirement.',
        maturity: 'Production',
    },
};

/**
 * Glossary of jargon that already appears in the copy above. Shape: term -> plain-English definition.
 */
window.SHOWCASE_GLOSSARY = {
    MCP: 'Model Context Protocol — the open standard this harness uses to expose and consume AI tools.',
    A2A: 'Agent-to-Agent — an open protocol for one AI agent to call another, with identity carried along.',
    JWT: 'JSON Web Token — a signed, compact envelope for identity claims.',
    'Entra ID': "Microsoft's cloud identity platform (formerly Azure AD).",
    BM25: 'A classic keyword-ranking algorithm used by search engines.',
    'Reciprocal Rank Fusion': 'A method for merging several ranked result lists into one combined ranking.',
    CRAG: 'Corrective Retrieval-Augmented Generation — scores retrieved results for trustworthiness before answering with them.',
    HyDE: 'Hypothetical Document Embeddings — generates a hypothetical answer first, then searches using that as the query.',
    OpenTelemetry: 'An open standard for collecting distributed traces, metrics, and logs.',
    'Hash-Chained Log': "A tamper-evident log where each entry's hash includes the previous entry's hash.",
    'Blast Radius': 'How much damage a single action could cause if it goes wrong.',
    'Leiden Community Detection': 'A graph algorithm for finding clusters of closely-related nodes.',
    'Progressive Disclosure': "Revealing information only as it's needed, instead of all at once.",
    'Multi-Tenant Isolation': "Keeping different users' or customers' data provably separate on shared infrastructure.",
    'Autonomy Tier': "A graded level of independence an agent is allowed to operate at before requiring a human's sign-off.",
    Sandbox: 'An isolated execution environment that limits what a process can access or affect.',
    EWMA: 'Exponentially Weighted Moving Average — a way of averaging recent data that weights newer points more heavily, good for spotting drift.',
    'LLM-as-Judge': "Using one AI model to grade another AI model's output quality.",
};
