// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.Diagnostics.Runtime.Utilities.Pdb
{
    public class PdbLines
    {
        public PdbSource File { get; private set; }
        public PdbLine[] Lines { get; private set; }

        internal PdbLines(PdbSource file, uint count)
        {
            File = file;
            Lines = new PdbLine[count];
        }
    }
}
