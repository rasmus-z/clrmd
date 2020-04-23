﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using Microsoft.Diagnostics.Runtime.Utilities;

namespace Microsoft.Diagnostics.Runtime
{
    /// <summary>
    /// Provides information about loaded modules in a <see cref="DataTarget"/>.
    /// </summary>
    public sealed class ModuleInfo
    {
        private bool? _isManaged;
        private VersionInfo? _version;
        private readonly bool _isVirtual;

        /// <summary>
        /// The DataTarget which contains this module.
        /// </summary>
        public DataTarget DataTarget { get; internal set; }

        /// <summary>
        /// Gets the base address of the object.
        /// </summary>
        public ulong ImageBase { get; }

        /// <summary>
        /// Gets the specific file size of the image used to index it on the symbol server.
        /// </summary>
        public int IndexFileSize { get; }

        /// <summary>
        /// Gets the timestamp of the image used to index it on the symbol server.
        /// </summary>
        public int IndexTimeStamp { get; }

        /// <summary>
        /// Gets the file name of the module on disk.
        /// </summary>
        public string? FileName { get; }

        /// <summary>
        /// Returns a <see cref="PEImage"/> from a stream constructed using instance fields of this object.
        /// If the PEImage cannot be constructed, <see langword="null"/> is returned.
        /// </summary>
        /// <returns></returns>
        public PEImage? GetPEImage()
        {
            try
            {
                PEImage image = new PEImage(new ReadVirtualStream(DataTarget.DataReader, (long)ImageBase, IndexFileSize), leaveOpen: false, isVirtual: _isVirtual);
                if (!_isManaged.HasValue)
                    _isManaged = image.IsManaged;

                return image.IsValid ? image : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the Linux BuildId of this module.  This will be <see langword="null"/> if the module does not have a BuildId.
        /// </summary>
        public ImmutableArray<byte> BuildId { get; }

        /// <summary>
        /// Gets a value indicating whether the module is managed.
        /// </summary>
        public bool IsManaged
        {
            get
            {
                if (!_isManaged.HasValue)
                {
                    // this can assign _isManaged
                    using PEImage? image = GetPEImage();

                    if (!_isManaged.HasValue)
                        _isManaged = image?.IsManaged ?? false;
                }

                return _isManaged.Value;
            }
        }

        public override string? ToString() => FileName;

        /// <summary>
        /// Gets the PDB associated with this module.
        /// </summary>
        public PdbInfo? Pdb
        {
            get
            {
                using PEImage? image = GetPEImage();
                if (image != null)
                {
                    if (!_isManaged.HasValue)
                        _isManaged = image.IsManaged;

                    return image.DefaultPdb;
                }

                return null;
            }
        }

        /// <summary>
        /// Gets the version information for this file.
        /// </summary>
        public VersionInfo Version
        {
            get
            {
                if (_version.HasValue)
                    return _version.Value;

                DataTarget.DataReader.GetVersionInfo(ImageBase, out VersionInfo version);
                _version = version;
                return version;
            }
        }


        // DataTarget is one of the few "internal set" properties, and is initialized as soon as DataTarget asks
        // IDataReader to create ModuleInfo.  So even though we don't set it here, we will immediately set the
        // value to non-null and never change it.

#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.

        /// <summary>
        /// Creates a ModuleInfo object with an IDataReader instance.  This is used when
        /// lazily evaluating VersionInfo.
        /// </summary>
        public ModuleInfo(ulong imgBase, int filesize, int timestamp, string? fileName, bool isVirtual, ImmutableArray<byte> buildId = default, VersionInfo? version = null)
        {
            ImageBase = imgBase;
            IndexFileSize = filesize;
            IndexTimeStamp = timestamp;
            FileName = fileName;
            _isVirtual = isVirtual;
            BuildId = buildId;
            _version = version;
        }
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
    }
}
