﻿using System;
using System.Collections.Generic;
using Jint.Native;
using Jint.Runtime.Interop;
using Raven.Client;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Operations.ETL;
using Raven.Server.Documents.Patch;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;

// ReSharper disable ForCanBeConvertedToForeach

namespace Raven.Server.Documents.ETL.Providers.Raven
{
    public class RavenEtlDocumentTransformer : EtlTransformer<RavenEtlItem, ICommandData>
    {
        private readonly Transformation _transformation;
        private readonly ScriptInput _script;
        private readonly List<ICommandData> _commands = new List<ICommandData>();
        private Dictionary<JsValue, HashSet<string>> _addedAttachments = null;

        public RavenEtlDocumentTransformer(Transformation transformation, DocumentDatabase database, DocumentsOperationContext context, ScriptInput script)
            : base(database, context, script.Transformation)
        {
            _transformation = transformation;
            _script = script;

            LoadToDestinations = _script.Transformation == null ? new string[0] : _script.LoadToCollections;
        }

        public override void Initalize()
        {
            base.Initalize();

            SingleRun?.ScriptEngine.SetValue(Transformation.AddAttachment, new ClrFunctionInstance(SingleRun.ScriptEngine, AddAttachment));
        }

        protected override string[] LoadToDestinations { get; }

        protected override void LoadToFunction(string collectionName, ScriptRunnerResult document)
        {
            if (collectionName == null)
                ThrowLoadParameterIsMandatory(nameof(collectionName));

            string id;
            var loadedToDifferentCollection = false;

            if (_script.IsLoadedToDefaultCollection(Current, collectionName))
            {
                id = Current.DocumentId;
            }
            else
            {
                id = GetPrefixedId(Current.DocumentId, collectionName);
                loadedToDifferentCollection = true;
            }

            var metadata = document.GetOrCreate(Constants.Documents.Metadata.Key);

            if (loadedToDifferentCollection || metadata.HasProperty(Constants.Documents.Metadata.Collection) == false)
                metadata.Put(Constants.Documents.Metadata.Collection, collectionName, throwOnError: true);

            if (metadata.HasProperty(Constants.Documents.Metadata.Id) == false)
                metadata.Put(Constants.Documents.Metadata.Id, id, throwOnError: true);

            if (metadata.HasProperty(Constants.Documents.Metadata.Attachments))
                metadata.Delete(Constants.Documents.Metadata.Attachments, throwOnError: true);

            var transformed = document.TranslateToObject(Context);

            var transformResult = Context.ReadObject(transformed, id);

            var transformationCommands = new List<ICommandData>();

            transformationCommands.Add(new PutCommandDataWithBlittableJson(id, null, transformResult));

            if (_transformation.IsHandlingAttachments && _addedAttachments != null && _addedAttachments.TryGetValue(document.Instance, out var addedAttachments))
            {
                if ((Current.Document.Flags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments)
                {
                    foreach (var attachment in addedAttachments)
                    {
                        var attachmentData =
                            Database.DocumentsStorage.AttachmentsStorage.GetAttachment(Context, Current.DocumentId, attachment, AttachmentType.Document, null);

                        if (attachmentData == null)
                            throw new InvalidOperationException($"Document '{Current.DocumentId}' doesn't have attachment named '{attachment}'");

                        transformationCommands.Add(new PutAttachmentCommandData(id, attachmentData.Name, attachmentData.Stream, attachmentData.ContentType,
                            null));
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Document '{Current.DocumentId}' doesn't have any attachment while the script tried to add the following ones: {string.Join(' ', addedAttachments)}");
                }
            }

            _commands.AddRange(transformationCommands);
        }

        private JsValue AddAttachment(JsValue self, JsValue[] args)
        {
            if (args.Length != 2 || args[1].IsString() == false)
                throw new InvalidOperationException($"{Transformation.AddAttachment}(obj, name) must have two arguments");

            if (_addedAttachments == null)
                _addedAttachments = new Dictionary<JsValue, HashSet<string>>();

            if (_addedAttachments.TryGetValue(args[0], out var attachments) == false)
            {
                attachments = new HashSet<string>();
                _addedAttachments.Add(args[0], attachments);
            }

            attachments.Add(args[1].AsString());

            return self;
        }

        private string GetPrefixedId(LazyStringValue documentId, string loadCollectionName)
        {
            return $"{documentId}/{_script.IdPrefixForCollection[loadCollectionName]}/";
        }

        public override IEnumerable<ICommandData> GetTransformedResults()
        {
            return _commands;
        }

        public override void Transform(RavenEtlItem item)
        {
            Current = item;

            if (item.IsDelete == false)
            {
                if (_script.Transformation != null)
                {
                    if (_script.LoadToCollections.Length > 1 || _script.IsLoadedToDefaultCollection(item, _script.LoadToCollections[0]) == false)
                    {
                        // first, we need to delete docs prefixed by modified document ID to properly handle updates of 
                        // documents loaded to non default collections

                        ApplyDeleteCommands(item, OperationType.Put);
                    }

                    SingleRun.Run(Context, Context, "execute", new object[] { Current.Document }).Dispose();
                }
                else
                {
                    _commands.Add(new PutCommandDataWithBlittableJson(item.DocumentId, null, item.Document.Data));

                    if ((item.Document.Flags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments)
                    {
                        HandleDocumentAttachments(item);
                    }
                }
            }
            else
            {
                if (_script.Transformation != null)
                    ApplyDeleteCommands(item, OperationType.Delete);
                else
                    _commands.Add(new DeleteCommandData(item.DocumentId, null));
            }
        }

        private void HandleDocumentAttachments(RavenEtlItem item)
        {
            if (item.Document.TryGetMetadata(out var metadata) == false ||
                metadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments) == false)
            {
                return;
            }

            metadata.Modifications = new DynamicJsonValue(metadata);
            metadata.Modifications.Remove(Constants.Documents.Metadata.Attachments);

            foreach (var attachment in attachments)
            {
                var attachmentInfo = (BlittableJsonReaderObject)attachment;

                if (attachmentInfo.TryGet(nameof(AttachmentName.Name), out string name))
                {
                    var attachmentData =
                        Database.DocumentsStorage.AttachmentsStorage.GetAttachment(Context, item.DocumentId, name, AttachmentType.Document, null);

                    _commands.Add(new PutAttachmentCommandData(item.DocumentId, attachmentData.Name, attachmentData.Stream, attachmentData.ContentType,
                        null));
                }
            }
        }

        private void ApplyDeleteCommands(RavenEtlItem item, OperationType operation)
        {
            for (var i = 0; i < _script.LoadToCollections.Length; i++)
            {
                var collection = _script.LoadToCollections[i];

                if (_script.IsLoadedToDefaultCollection(item, collection))
                {
                    if (operation == OperationType.Delete || _transformation.IsHandlingAttachments)
                        _commands.Add(new DeleteCommandData(item.DocumentId, null));
                }
                else
                    _commands.Add(new DeletePrefixedCommandData(GetPrefixedId(item.DocumentId, collection)));
            }
        }

        public class ScriptInput
        {
            private readonly Dictionary<string, Dictionary<string, bool>> _collectionNameComparisons;

            public readonly string[] LoadToCollections = new string[0];

            public readonly PatchRequest Transformation;

            public readonly HashSet<string> DefaultCollections;

            public readonly Dictionary<string, string> IdPrefixForCollection = new Dictionary<string, string>();

            public ScriptInput(Transformation transformation)
            {
                DefaultCollections = new HashSet<string>(transformation.Collections, StringComparer.OrdinalIgnoreCase);

                if (string.IsNullOrEmpty(transformation.Script))
                    return;

                Transformation = new PatchRequest(transformation.Script, PatchRequestType.RavenEtl);

                LoadToCollections = transformation.GetCollectionsFromScript();

                foreach (var collection in LoadToCollections)
                {
                    IdPrefixForCollection[collection] = DocumentConventions.DefaultTransformCollectionNameToDocumentIdPrefix(collection);
                }

                if (transformation.Collections == null)
                    return;

                _collectionNameComparisons = new Dictionary<string, Dictionary<string, bool>>(transformation.Collections.Count);

                foreach (var sourceCollection in transformation.Collections)
                {
                    _collectionNameComparisons[sourceCollection] = new Dictionary<string, bool>(transformation.Collections.Count);

                    foreach (var loadToCollection in LoadToCollections)
                    {
                        _collectionNameComparisons[sourceCollection][loadToCollection] = string.Compare(sourceCollection, loadToCollection, StringComparison.OrdinalIgnoreCase) == 0;
                    }
                }
            }

            public bool IsLoadedToDefaultCollection(RavenEtlItem item, string loadToCollection)
            {
                if (item.Collection != null)
                    return _collectionNameComparisons[item.Collection][loadToCollection];

                var collection = item.CollectionFromMetadata;

                return collection?.CompareTo(loadToCollection) == 0;
            }
        }

        private enum OperationType
        {
            Put,
            Delete
        }
    }
}
