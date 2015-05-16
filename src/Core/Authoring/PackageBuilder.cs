﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using NuGet.Resources;

namespace NuGet
{
    public class PackageBuilder : IPackageBuilder
    {
        public static readonly int MaxSupportedQuirksModeVersion = 1;
        private const string DefaultContentType = "application/octet";
        internal const string ManifestRelationType = "manifest";
        private readonly bool _includeEmptyDirectories;
        private PackageType _packageType;
        private Version _minClientVersion;

        public PackageBuilder(string path, IPropertyProvider propertyProvider, bool includeEmptyDirectories, PackageType packageType)
            : this(path, Path.GetDirectoryName(path), propertyProvider, includeEmptyDirectories, packageType)
        {
        }

        public PackageBuilder(string path, string basePath, IPropertyProvider propertyProvider, bool includeEmptyDirectories, PackageType packageType)
            : this(includeEmptyDirectories, packageType)
        {
            using (Stream stream = File.OpenRead(path))
            {
                ReadManifest(stream, basePath, propertyProvider);
            }
        }

        public PackageBuilder(Stream stream, string basePath, IPropertyProvider propertyProvider, bool includeEmptyDirectories, PackageType packageType)
            : this(includeEmptyDirectories, packageType)
        {
            ReadManifest(stream, basePath, propertyProvider);
        }

        public PackageBuilder()
            : this(includeEmptyDirectories: false, packageType: null)
        {
        }

        private PackageBuilder(bool includeEmptyDirectories, PackageType packageType)
        {
            _includeEmptyDirectories = includeEmptyDirectories;
            PackageType = packageType;
            Files = new Collection<IPackageFile>();
            DependencySets = new Collection<PackageDependencySet>();
            FrameworkReferences = new Collection<FrameworkAssemblyReference>();
            PackageAssemblyReferences = new Collection<PackageReferenceSet>();
            Authors = new HashSet<string>();
            Owners = new HashSet<string>();
            Tags = new HashSet<string>();
        }

        public string Id
        {
            get;
            set;
        }

        public SemanticVersion Version
        {
            get;
            set;
        }

        public string Title
        {
            get;
            set;
        }

        public ISet<string> Authors
        {
            get;
            private set;
        }

        public ISet<string> Owners
        {
            get;
            private set;
        }

        public Uri IconUrl
        {
            get;
            set;
        }

        public Uri LicenseUrl
        {
            get;
            set;
        }

        public Uri ProjectUrl
        {
            get;
            set;
        }

        public bool RequireLicenseAcceptance
        {
            get;
            set;
        }

        public bool DevelopmentDependency
        {
            get;
            set;
        }

        public string Description
        {
            get;
            set;
        }

        public string Summary
        {
            get;
            set;
        }

        public string ReleaseNotes
        {
            get;
            set;
        }

        public string Language
        {
            get;
            set;
        }

        public ISet<string> Tags
        {
            get;
            private set;
        }

        public string Copyright
        {
            get;
            set;
        }

        public Collection<PackageDependencySet> DependencySets
        {
            get;
            private set;
        }

        public Collection<IPackageFile> Files
        {
            get;
            private set;
        }

        public Collection<FrameworkAssemblyReference> FrameworkReferences
        {
            get;
            private set;
        }

        public ICollection<PackageReferenceSet> PackageAssemblyReferences
        {
            get;
            private set;
        }

        IEnumerable<string> IPackageMetadata.Authors
        {
            get
            {
                return Authors;
            }
        }

        IEnumerable<string> IPackageMetadata.Owners
        {
            get
            {
                return Owners;
            }
        }

        string IPackageMetadata.Tags
        {
            get
            {
                return String.Join(" ", Tags);
            }
        }

        IEnumerable<PackageDependencySet> IPackageMetadata.DependencySets
        {
            get
            {
                return DependencySets;
            }
        }

        IEnumerable<FrameworkAssemblyReference> IPackageMetadata.FrameworkAssemblies
        {
            get
            {
                return FrameworkReferences;
            }
        }

        public Version MinClientVersion
        {
            get
            {
                return _minClientVersion;
            }
            set
            {
                if (PackageType != PackageType.Default)
                {
                    if (value == null)
                    {
                        // No-op this since the calling code was attempting to initialize this.
                    }
                    else if (value < Constants.ManagedCodeConventionsClientVersion)
                    {
                        // If a specific minClientVersion is requested, throw.
                        throw new ArgumentOutOfRangeException("value",
                            string.Format(CultureInfo.CurrentCulture, NuGetResources.MinClientVersion_MustBeHigher, Constants.ManagedCodeConventionsClientVersion));
                    }
                    else
                    {
                        _minClientVersion = value;
                    }
                }
                else
                {
                    _minClientVersion = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the version of quirks mode that this package supports.
        /// </summary>
        public PackageType PackageType
        {
            get { return _packageType ?? PackageType.Default; }
            set
            {
                if (value != null && value != PackageType.Default)
                {
                    if (!string.Equals(value.Name, PackageType.Managed.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            String.Format(CultureInfo.CurrentCulture, NuGetResources.UnsupportedPackageType, value.Name));
                    }

                    if (_minClientVersion == null || _minClientVersion < Constants.ManagedCodeConventionsClientVersion)
                    {
                        _minClientVersion = Constants.ManagedCodeConventionsClientVersion;
                    }
                }

                _packageType = value;
            }
        }

        public void Save(Stream stream)
        {
            // Make sure we're saving a valid package id
            PackageIdValidator.ValidatePackageId(Id);

            // Throw if the package doesn't contain any dependencies nor content
            if (!Files.Any() && !DependencySets.SelectMany(d => d.Dependencies).Any() && !FrameworkReferences.Any())
            {
                throw new InvalidOperationException(NuGetResources.CannotCreateEmptyPackage);
            }

            if (!ValidateSpecialVersionLength(Version))
            {
                throw new InvalidOperationException(NuGetResources.SemVerSpecialVersionTooLong);
            }

            ValidateDependencySets(Version, DependencySets);
            ValidateReferenceAssemblies(Files, PackageAssemblyReferences);
            ValidatePackageType(Files, PackageType);

            using (Package package = Package.Open(stream, FileMode.Create))
            {
                // Validate and write the manifest
                WriteManifest(package, DetermineMinimumSchemaVersion(Files));

                // Write the files to the package
                WriteFiles(package);

                // Copy the metadata properties back to the package
                package.PackageProperties.Creator = String.Join(",", Authors);
                package.PackageProperties.Description = Description;
                package.PackageProperties.Identifier = Id;
                package.PackageProperties.Version = Version.ToString();
                package.PackageProperties.Language = Language;
                package.PackageProperties.Keywords = ((IPackageMetadata)this).Tags;
                package.PackageProperties.Title = Title;
                package.PackageProperties.LastModifiedBy = CreatorInfo();
            }
        }

        private static void ValidatePackageType(IEnumerable<IPackageFile> packageFiles, PackageType packageType)
        {
            if (packageType != PackageType.Default)
            {
                if (!string.Equals(PackageType.Managed.Name, packageType.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.UnsupportedPackageType, packageType.Name));
                }
                else
                {
                    if (packageType.Version != PackageType.Managed.Version)
                    {
                        throw new InvalidOperationException(String.Format(
                            CultureInfo.CurrentCulture,
                            NuGetResources.UnsupportedPackageTypeVersion,
                            packageType.Version));
                    }
                    else
                    {
                        var filesWithoutTFM = packageFiles.Where(file => file.TargetFramework == null)
                            .Select(file => file.Path);

                        if (filesWithoutTFM.Any())
                        {
                            var paths = String.Join(", ", filesWithoutTFM);
                            throw new InvalidOperationException(
                                String.Format(CultureInfo.CurrentCulture, NuGetResources.StrictTfm_UnsupportedFilePath, paths));
                        }
                    }
                }
            }
        }

        private static string CreatorInfo()
        {
            List<string> creatorInfo = new List<string>();
            var assembly = typeof(PackageBuilder).Assembly;
            creatorInfo.Add(assembly.FullName);
            creatorInfo.Add(Environment.OSVersion.ToString());

            var attributes = assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), true);
            if (attributes.Length > 0)
            {
                var attribute = (System.Runtime.Versioning.TargetFrameworkAttribute)attributes[0];
                creatorInfo.Add(attribute.FrameworkDisplayName);
            }

            return String.Join(";", creatorInfo);
        }

        private static int DetermineMinimumSchemaVersion(Collection<IPackageFile> Files)
        {
            if (HasXdtTransformFile(Files))
            {
                // version 5
                return ManifestVersionUtility.XdtTransformationVersion;
            }

            if (RequiresV4TargetFrameworkSchema(Files))
            {
                // version 4
                return ManifestVersionUtility.TargetFrameworkSupportForDependencyContentsAndToolsVersion;
            }

            return ManifestVersionUtility.DefaultVersion;
        }

        private static bool RequiresV4TargetFrameworkSchema(ICollection<IPackageFile> files)
        {
            // check if any file under Content or Tools has TargetFramework defined
            bool hasContentOrTool = files.Any(
                f => f.TargetFramework != null &&
                     f.TargetFramework != VersionUtility.UnsupportedFrameworkName &&
                     (f.Path.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                      f.Path.StartsWith(Constants.ToolsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

            if (hasContentOrTool)
            {
                return true;
            }

            // now check if the Lib folder has any empty framework folder
            bool hasEmptyLibFolder = files.Any(
                f => f.TargetFramework != null &&
                     f.Path.StartsWith(Constants.LibDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                     f.EffectivePath == Constants.PackageEmptyFileName);

            return hasEmptyLibFolder;
        }

        private static bool HasXdtTransformFile(ICollection<IPackageFile> contentFiles)
        {
            return contentFiles.Any(file =>
                file.Path != null &&
                file.Path.StartsWith(Constants.ContentDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                (file.Path.EndsWith(".install.xdt", StringComparison.OrdinalIgnoreCase) ||
                 file.Path.EndsWith(".uninstall.xdt", StringComparison.OrdinalIgnoreCase)));
        }

        internal static void ValidateDependencySets(SemanticVersion version, IEnumerable<PackageDependencySet> dependencies)
        {
            if (version == null)
            {
                // We have independent validation for null-versions.
                return;
            }

            foreach (var dep in dependencies.SelectMany(s => s.Dependencies))
            {
                PackageIdValidator.ValidatePackageId(dep.Id);
            }

            if (String.IsNullOrEmpty(version.SpecialVersion))
            {
                // If we are creating a production package, do not allow any of the dependencies to be a prerelease version.
                var prereleaseDependency = dependencies.SelectMany(set => set.Dependencies).FirstOrDefault(IsPrereleaseDependency);
                if (prereleaseDependency != null)
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.Manifest_InvalidPrereleaseDependency, prereleaseDependency.ToString()));
                }
            }
        }

        internal static void ValidateReferenceAssemblies(IEnumerable<IPackageFile> files, IEnumerable<PackageReferenceSet> packageAssemblyReferences)
        {
            var libFiles = new HashSet<string>(from file in files
                                               where !String.IsNullOrEmpty(file.Path) && file.Path.StartsWith("lib", StringComparison.OrdinalIgnoreCase)
                                               select Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase);

            foreach (var reference in packageAssemblyReferences.SelectMany(p => p.References))
            {
                if (!libFiles.Contains(reference) &&
                    !libFiles.Contains(reference + ".dll") &&
                    !libFiles.Contains(reference + ".exe") &&
                    !libFiles.Contains(reference + ".winmd"))
                {
                    throw new InvalidDataException(String.Format(CultureInfo.CurrentCulture, NuGetResources.Manifest_InvalidReference, reference));
                }
            }
        }

        private void ReadManifest(Stream stream, string basePath, IPropertyProvider propertyProvider)
        {
            // Deserialize the document and extract the metadata
            Manifest manifest = Manifest.ReadFrom(stream, propertyProvider, validateSchema: true);

            Populate(manifest.Metadata);

            // If there's no base path then ignore the files node
            if (basePath != null)
            {
                if (manifest.Files == null)
                {
                    AddFiles(basePath, @"**\*", null);
                }
                else
                {
                    PopulateFiles(basePath, manifest.Files);
                }
            }
        }

        public void Populate(ManifestMetadata manifestMetadata)
        {
            IPackageMetadata metadata = manifestMetadata;
            Id = metadata.Id;
            Version = metadata.Version;
            Title = metadata.Title;
            Authors.AddRange(metadata.Authors);
            Owners.AddRange(metadata.Owners);
            IconUrl = metadata.IconUrl;
            LicenseUrl = metadata.LicenseUrl;
            ProjectUrl = metadata.ProjectUrl;
            RequireLicenseAcceptance = metadata.RequireLicenseAcceptance;
            DevelopmentDependency = metadata.DevelopmentDependency;
            Description = metadata.Description;
            Summary = metadata.Summary;
            ReleaseNotes = metadata.ReleaseNotes;
            Language = metadata.Language;
            Copyright = metadata.Copyright;
            MinClientVersion = metadata.MinClientVersion;

            if (_packageType == null)
            {
                // If the PackageType was previous set as part of initializing the PackageBuilder,
                // do not override it with values from the manifest.
                PackageType = metadata.PackageType;
            }

            if (metadata.Tags != null)
            {
                Tags.AddRange(ParseTags(metadata.Tags));
            }

            DependencySets.AddRange(metadata.DependencySets);
            FrameworkReferences.AddRange(metadata.FrameworkAssemblies);

            if (manifestMetadata.ReferenceSets != null)
            {
                PackageAssemblyReferences.AddRange(manifestMetadata.ReferenceSets.Select(r => new PackageReferenceSet(r, this.UsesManagedCodeConventions())));
            }
        }

        public void PopulateFiles(string basePath, IEnumerable<ManifestFile> files)
        {
            foreach (var file in files)
            {
                AddFiles(basePath, file.Source, file.Target, file.Exclude);
            }
        }

        private void WriteManifest(Package package, int minimumManifestVersion)
        {
            Uri uri = UriUtility.CreatePartUri(Id + Constants.ManifestExtension);

            // Create the manifest relationship
            package.CreateRelationship(uri, TargetMode.Internal, Constants.PackageRelationshipNamespace + ManifestRelationType);

            // Create the part
            PackagePart packagePart = package.CreatePart(uri, DefaultContentType, CompressionOption.Maximum);

            using (Stream stream = packagePart.GetStream())
            {
                Manifest manifest = Manifest.Create(this);
                manifest.Save(stream, minimumManifestVersion);
            }
        }

        private void WriteFiles(Package package)
        {
            // Add files that might not come from expanding files on disk
            foreach (IPackageFile file in new HashSet<IPackageFile>(Files))
            {
                using (Stream stream = file.GetStream())
                {
                    try
                    {
                        CreatePart(package, file.Path, stream);
                    }
                    catch
                    {
                        Console.WriteLine(file.Path);
                        throw;
                    }
                }
            }

            foreach (var file in package.GetParts().GroupBy(s => s.Uri).Where(_ => _.Count() > 1))
            {
                Console.WriteLine(file.Key);
            }
        }

        private void AddFiles(string basePath, string source, string destination, string exclude = null)
        {
            var useManagedCodeConventions = this.UsesManagedCodeConventions();
            List<PhysicalPackageFile> searchFiles = PathResolver.ResolveSearchPattern(
                    basePath: basePath,
                    searchPath: source,
                    targetPath: destination,
                    includeEmptyDirectories: _includeEmptyDirectories,
                    useManagedCodeConventions: useManagedCodeConventions)
                .ToList();
            if (_includeEmptyDirectories)
            {
                // we only allow empty directories which are legit framework folders.
                searchFiles.RemoveAll(file => file.TargetFramework == null &&
                                              Path.GetFileName(file.TargetPath) == Constants.PackageEmptyFileName);
            }

            ExcludeFiles(searchFiles, basePath, exclude);

            if (!PathResolver.IsWildcardSearch(source) && !PathResolver.IsDirectoryPath(source) && !searchFiles.Any())
            {
                throw new FileNotFoundException(
                    String.Format(CultureInfo.CurrentCulture, NuGetResources.PackageAuthoring_FileNotFound, source));
            }


            Files.AddRange(searchFiles);
        }

        private static void ExcludeFiles(List<PhysicalPackageFile> searchFiles, string basePath, string exclude)
        {
            if (String.IsNullOrEmpty(exclude))
            {
                return;
            }

            // One or more exclusions may be specified in the file. Split it and prepend the base path to the wildcard provided.
            var exclusions = exclude.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in exclusions)
            {
                string wildCard = PathResolver.NormalizeWildcardForExcludedFiles(basePath, item);
                PathResolver.FilterPackageFiles(searchFiles, p => p.SourcePath, new[] { wildCard });
            }
        }

        private static void CreatePart(Package package, string path, Stream sourceStream)
        {
            if (PackageHelper.IsManifest(path))
            {
                return;
            }

            Uri uri = UriUtility.CreatePartUri(path);

            // Create the part
            PackagePart packagePart = package.CreatePart(uri, DefaultContentType, CompressionOption.Maximum);
            using (Stream stream = packagePart.GetStream())
            {
                sourceStream.CopyTo(stream);
            }
        }

        /// <summary>
        /// Tags come in this format. tag1 tag2 tag3 etc..
        /// </summary>
        private static IEnumerable<string> ParseTags(string tags)
        {
            Debug.Assert(tags != null);
            return from tag in tags.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                   select tag.Trim();
        }

        private static bool IsPrereleaseDependency(PackageDependency dependency)
        {
            var versionSpec = dependency.VersionSpec;
            if (versionSpec != null)
            {
                return (versionSpec.MinVersion != null && !String.IsNullOrEmpty(dependency.VersionSpec.MinVersion.SpecialVersion)) ||
                       (versionSpec.MaxVersion != null && !String.IsNullOrEmpty(dependency.VersionSpec.MaxVersion.SpecialVersion));
            }
            return false;
        }

        private static bool ValidateSpecialVersionLength(SemanticVersion version)
        {
            return version == null || version.SpecialVersion == null || version.SpecialVersion.Length <= 20;
        }
    }
}