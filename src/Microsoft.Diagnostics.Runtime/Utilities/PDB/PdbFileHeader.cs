// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Text;

namespace Microsoft.Diagnostics.Runtime.Utilities.Pdb
{
    internal class PdbFileHeader
    {
        //internal PdbFileHeader(int pageSize) {
        //  this.magic = new byte[32] {
        //            0x4D, 0x69, 0x63, 0x72, 0x6F, 0x73, 0x6F, 0x66, // "Microsof"
        //            0x74, 0x20, 0x43, 0x2F, 0x43, 0x2B, 0x2B, 0x20, // "t C/C++ "
        //            0x4D, 0x53, 0x46, 0x20, 0x37, 0x2E, 0x30, 0x30, // "MSF 7.00"
        //            0x0D, 0x0A, 0x1A, 0x44, 0x53, 0x00, 0x00, 0x00  // "^^^DS^^^"
        //        };
        //  this.pageSize = pageSize;
        //}

        internal PdbFileHeader(Stream reader, BitAccess bits)
        {
            bits.MinCapacity(56);
            reader.Seek(0, SeekOrigin.Begin);
            bits.FillBuffer(reader, 52);

            this.Magic = new byte[32];
            bits.ReadBytes(this.Magic);                 //   0..31
            bits.ReadInt32(out this.PageSize);          //  32..35
            bits.ReadInt32(out this.FreePageMap);       //  36..39
            bits.ReadInt32(out this.PagesUsed);         //  40..43
            bits.ReadInt32(out this.DirectorySize);     //  44..47
            bits.ReadInt32(out this.Zero);              //  48..51

            int directoryPages = ((((DirectorySize + PageSize - 1) / PageSize) * 4) + PageSize - 1) / PageSize;
            this.DirectoryRoot = new int[directoryPages];
            bits.FillBuffer(reader, directoryPages * 4);
            bits.ReadInt32(this.DirectoryRoot);
        }

        //internal string Magic {
        //  get { return StringFromBytesUTF8(magic); }
        //}

        //internal void Write(Stream writer, BitAccess bits) {
        //  bits.MinCapacity(pageSize);
        //  bits.WriteBytes(magic);                     //   0..31
        //  bits.WriteInt32(pageSize);                  //  32..35
        //  bits.WriteInt32(freePageMap);               //  36..39
        //  bits.WriteInt32(pagesUsed);                 //  40..43
        //  bits.WriteInt32(directorySize);             //  44..47
        //  bits.WriteInt32(zero);                      //  48..51
        //  bits.WriteInt32(directoryRoot);             //  52..55

        //  writer.Seek(0, SeekOrigin.Begin);
        //  bits.WriteBuffer(writer, pageSize);
        //}

        //////////////////////////////////////////////////// Helper Functions.
        //
        //internal static string StringFromBytesUTF8(byte[] bytes) {
        //  return StringFromBytesUTF8(bytes, 0, bytes.Length);
        //}

        //internal static string StringFromBytesUTF8(byte[] bytes, int offset, int length) {
        //  for (int i = 0; i < length; i++) {
        //    if (bytes[offset + i] < ' ') {
        //      length = i;
        //    }
        //  }
        //  return Encoding.UTF8.GetString(bytes, offset, length);
        //}

        ////////////////////////////////////////////////////////////// Fields.
        //
        internal readonly byte[] Magic;
        internal readonly int PageSize;
        internal int FreePageMap;
        internal int PagesUsed;
        internal int DirectorySize;
        internal readonly int Zero;
        internal int[] DirectoryRoot;
    }
}
