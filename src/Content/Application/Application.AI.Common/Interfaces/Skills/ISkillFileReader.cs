namespace Application.AI.Common.Interfaces.Skills;

/// <summary>
/// Sandboxed, <b>read-only</b> access to skill content on disk: <c>SKILL.md</c> manifests, the
/// directories they are discovered in, and the supporting files a skill discloses on demand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than reusing <c>IFileSystemService</c>.</b> Skill content lives outside
/// the model's file sandbox by default — the shipped configuration allows <c>workspace</c> while
/// skills live in <c>skills</c> — so routing skill loading through that service would have required
/// adding the skill roots to it. That service can write, and the model reaches it through the
/// <c>file_system</c> tool with no approval gate on writes, so the widening would have handed the
/// model the ability to rewrite its own <c>SKILL.md</c> files, including the <c>allowed-tools</c>
/// list that constrains which tools it may call. This interface closes the original bypass — skill
/// reads that went straight to <c>System.IO</c> and were confined by nothing — without opening that
/// one (issue #247).
/// </para>
/// <para>
/// <b>The guarantee.</b> Every path handed to an implementation is confined to the configured skill
/// content roots: <c>AppConfig:AI:Skills</c>, <c>AppConfig:AI:Agents</c> (agent-owned skills live in
/// <c>&lt;agentDir&gt;/skills</c>), and the bundle staging root. The set is resolved <em>live</em>
/// rather than snapshotted at startup, because plugin-supplied skill directories are appended to the
/// skill configuration by <c>PluginStartupLoader</c> after the container is built — a snapshot taken
/// at registration time would refuse exactly the plugin skills the harness advertises support for.
/// </para>
/// <para>
/// <b>Refusals are loud.</b> A path outside the roots throws
/// <see cref="Exceptions.SkillPathRefusedException"/> from every member, including the existence
/// probes. That type is distinct from a plain <see cref="UnauthorizedAccessException"/> on purpose:
/// the operating system raises the latter for an ordinary permission denial on a directory that is
/// legitimately inside the sandbox, and callers must be able to tolerate that while treating a
/// sandbox refusal as fatal. Returning <see langword="false"/> for a refused
/// path would let a misconfigured root read as "this skill has no manifest", turning a security
/// refusal into a silently empty skill set.
/// </para>
/// </remarks>
public interface ISkillFileReader
{
    /// <summary>
    /// Reads a skill file as UTF-8 text.
    /// </summary>
    /// <remarks>
    /// Synchronous because skill discovery is a synchronous, cache-filling startup walk
    /// (<c>ISkillMetadataRegistry</c> exposes a synchronous surface to every consumer). Use
    /// <see cref="ReadTextAsync"/> on the per-turn disclosure path instead.
    /// </remarks>
    /// <param name="path">An absolute path inside a configured skill content root.</param>
    /// <returns>The file's contents.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, or is rejected by the sandbox's input validation — which refuses
    /// traversal sequences, so a relative path must be resolved to an absolute one first.
    /// </exception>
    /// <exception cref="Exceptions.SkillPathRefusedException">The path is outside the skill content roots.</exception>
    /// <exception cref="FileNotFoundException">The path is permitted but names no file.</exception>
    /// <exception cref="IOException">The file exceeds the read size limit.</exception>
    string ReadText(string path);

    /// <summary>
    /// Reads a skill file as UTF-8 text, asynchronously.
    /// </summary>
    /// <remarks>
    /// Used for on-demand resource disclosure, which the agent framework invokes per turn as a
    /// deferred <c>Func&lt;Task&lt;string&gt;&gt;</c> while the model is waiting.
    /// </remarks>
    /// <param name="path">An absolute path inside a configured skill content root.</param>
    /// <param name="cancellationToken">Token to observe while reading.</param>
    /// <returns>The file's contents.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, or is rejected by the sandbox's input validation — which refuses
    /// traversal sequences, so a relative path must be resolved to an absolute one first.
    /// </exception>
    /// <exception cref="Exceptions.SkillPathRefusedException">The path is outside the skill content roots.</exception>
    /// <exception cref="FileNotFoundException">The path is permitted but names no file.</exception>
    /// <exception cref="IOException">The file exceeds the read size limit.</exception>
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a permitted path names an existing file.
    /// </summary>
    /// <param name="path">An absolute path inside a configured skill content root.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, or is rejected by the sandbox's input validation — which refuses
    /// traversal sequences, so a relative path must be resolved to an absolute one first.
    /// </exception>
    /// <exception cref="Exceptions.SkillPathRefusedException">The path is outside the skill content roots.</exception>
    bool FileExists(string path);

    /// <summary>
    /// Whether a permitted path names an existing directory.
    /// </summary>
    /// <param name="path">An absolute path inside a configured skill content root.</param>
    /// <returns><see langword="true"/> when the directory exists.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, or is rejected by the sandbox's input validation — which refuses
    /// traversal sequences, so a relative path must be resolved to an absolute one first.
    /// </exception>
    /// <exception cref="Exceptions.SkillPathRefusedException">The path is outside the skill content roots.</exception>
    bool DirectoryExists(string path);

    /// <summary>
    /// Lists the immediate subdirectories of a permitted directory, as absolute paths.
    /// </summary>
    /// <remarks>
    /// Subdirectories that the sandbox refuses — a junction pointing outside the skill roots, for
    /// example — are omitted rather than returned, so a caller walking the result cannot be led out
    /// of the sandbox by following one.
    /// </remarks>
    /// <param name="path">An absolute path inside a configured skill content root.</param>
    /// <returns>Absolute paths of the immediate subdirectories, empty when there are none.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, or is rejected by the sandbox's input validation — which refuses
    /// traversal sequences, so a relative path must be resolved to an absolute one first.
    /// </exception>
    /// <exception cref="Exceptions.SkillPathRefusedException">The path is outside the skill content roots.</exception>
    /// <exception cref="DirectoryNotFoundException">The path is permitted but names no directory.</exception>
    IReadOnlyList<string> EnumerateDirectories(string path);
}
