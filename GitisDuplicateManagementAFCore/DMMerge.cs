using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.IO;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.Messages;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

namespace GitisDuplicateManagementAFCore
{
    public class DMMergeRequest
    {
        Entity SourceEntity { get; set; }
        Entity MasterEntity { get; set; }
        List<Entity> DuplicateEntitieis { get; set; }
        Entity NewMasterEntity { get; set; }
        EntityReference TargetEntityRef { get; set; }
        public MergeOption MergeType { get; private set; }

        IOrganizationService service;
        string SourceRecordUrl = "";
        DMLog _DmLog { get; set; }
        MergeConfig _MergeConfig;
        bool _MasterRecordClosed = false;
        DMRetrieveResponse _DmRetrieveResponse = null;
        bool _IsMasterSubscriber = false;
        bool _IsMasterOwnedByAdviser = false;
        bool _IsAnyRecordOwnedByAdvser = false;

        public DMMergeRequest(DMRetrieveResponse dmRetrieveResponse, DMLog Dmlog, MergeConfig Mergeconfig)
        {
            _DmRetrieveResponse = dmRetrieveResponse;
            _DmLog = Dmlog;
            _MergeConfig = Mergeconfig;

            SourceEntity = dmRetrieveResponse.SourceEntity;
            MasterEntity = dmRetrieveResponse.MasterEntity;
            DuplicateEntitieis = dmRetrieveResponse.DuplicateEntities;

            if (SourceEntity.Id != MasterEntity.Id)
            {
                if (!DuplicateEntitieis.Contains(SourceEntity))
                {
                    DuplicateEntitieis.Add(SourceEntity);
                }
                if (DuplicateEntitieis.Contains(MasterEntity))
                {
                    DuplicateEntitieis.Remove(MasterEntity);
                }
                Dmlog.IsMaster = false;
            }
            else Dmlog.IsMaster = true;


            NewMasterEntity = new Entity(MasterEntity.LogicalName, MasterEntity.Id);
            TargetEntityRef = new EntityReference(MasterEntity.LogicalName, MasterEntity.Id);

            SourceRecordUrl = Environment.GetEnvironmentVariable("D365_APPURL") + "&pagetype=entityrecord&etn=contact&id=" + SourceEntity.Id.ToString();

        }

        public void LogPreMerge(IOrganizationService Service)
        {

            service = Service;
            //Record URL
            _DmLog.RecordURL = Environment.GetEnvironmentVariable("D365_APPURL") + "&pagetype=entityrecord&etn=contact&id=" + SourceEntity.Id.ToString();

            EntityReference ownerEr = (EntityReference)MasterEntity["ownerid"];

            //Candidate Adviser Status / Open = 222750000 / Closed = 222750001
            //Candidate Status	/Assigned = 222750002

            int candidateStatusMaster = 0;
            if (MasterEntity.Attributes.Contains("dfe_candidatestatus")) candidateStatusMaster = ((OptionSetValue)MasterEntity["dfe_candidatestatus"]).Value;

            int adviserStatusMaster = 0;
            if (MasterEntity.Attributes.Contains("dfe_candidateadviserstatus")) adviserStatusMaster = ((OptionSetValue)MasterEntity["dfe_candidateadviserstatus"]).Value;

            int adviserStatusReasonMaster = 0;
            if (MasterEntity.Attributes.Contains("dfe_candidateadviserstatusreason")) adviserStatusReasonMaster = ((OptionSetValue)MasterEntity["dfe_candidateadviserstatusreason"]).Value;


            //Master Record Closed
            if (adviserStatusMaster == 222750001) _DmLog.MasterRecordClosed = true;

            //Master has ITT Applied To 
            var masterItt = _DmRetrieveResponse.ITTAppliedToList.FirstOrDefault(f => f.ContactId == MasterEntity.Id);
            if (masterItt != null && (masterItt.DateAccepted != DateTime.MinValue || masterItt.DateApplied != DateTime.MinValue)) _DmLog.MasterHasITT = true;


            //Master has release date
            if (MasterEntity.Attributes.Contains("dfe_datereleased")) _DmLog.MasterHasReleaseDate = true;

            //MultipleOwners //SES Overwrite
            int noOfDuplicateAssignedAndOpen = 0;
            bool sesOverWrite = false;

            if (IsOwnedByAdviser(MasterEntity) && candidateStatusMaster == 222750002) noOfDuplicateAssignedAndOpen += 1;

            foreach (Entity entity in DuplicateEntitieis)
            {
                int candidateStatusDuplicate = 0;
                if (entity.Attributes.Contains("dfe_candidatestatus")) candidateStatusDuplicate = ((OptionSetValue)entity["dfe_candidatestatus"]).Value;
                int adviserStatusDuplicate = 0;
                if (entity.Attributes.Contains("dfe_candidateadviserstatus")) adviserStatusDuplicate = ((OptionSetValue)entity["dfe_candidateadviserstatus"]).Value;

                if (candidateStatusMaster == 222750002) //Assigned
                {
                    if (adviserStatusMaster == 222750000 && IsOwnedByAdviser(entity) == true) noOfDuplicateAssignedAndOpen += 1;
                    if (noOfDuplicateAssignedAndOpen > 1 && _DmLog.MultipleOwner == false)
                    {
                        _DmLog.MultipleOwner = true;
                    }
                }

                if (sesOverWrite == false) sesOverWrite = SESOverwrite(entity);
                if (_IsMasterSubscriber == false) ServiceSubscriptionOverwrite(entity);


                //duplicate has release date
                if (entity.Attributes.Contains("dfe_datereleased") && _DmLog.DuplicateHasReleaseDate == false) _DmLog.DuplicateHasReleaseDate = true;

                //Duplicate has ITT Applied To 
                if (_DmLog.DuplicateHasITT == false)
                {
                    var itt = _DmRetrieveResponse.ITTAppliedToList.FirstOrDefault(f => f.ContactId == entity.Id);
                    if (itt != null && (itt.DateAccepted != DateTime.MinValue || itt.DateApplied != DateTime.MinValue)) _DmLog.DuplicateHasITT = true;

                }
                //Duplicate Owner 
                if (IsOwnedByAdviser(entity) == true && _DmLog.HasDuplicateOwnedByAdviser == false)
                {
                    _DmLog.HasDuplicateOwnedByAdviser = true;
                    if (entity.Attributes.Contains("dfe_candidateadviserstatus")) _DmLog.DuplicateCandicateAdviserStatus = entity.FormattedValues["dfe_candidateadviserstatus"];

                }

                //Is Duplicate Subscriber

                if (entity.Attributes.Contains("dfe_gitisttaservicestartdate") ||
                              entity.Attributes.Contains("dfe_gitismailinglistservicestartdate") ||
                              entity.Attributes.Contains("dfe_gitiseventsservicestartdate"))
                {
                    _DmLog.IsDuplicateSubscriber = true;
                }

            }

            _DmLog.SESOverwrite = sesOverWrite;

            //DoNotOverwrite Master
            if (candidateStatusMaster == 222750002)
            {
                if (adviserStatusMaster == 222750000 && IsOwnedByAdviser(MasterEntity) == true)
                {
                    MergeType = MergeOption.Merge_DoNotOverwriteMaster;
                    _DmLog.DoNotOverwriteMaster = true;
                }
            }

            //Check if master is already a subscriber
            if (MasterEntity.Attributes.Contains("dfe_gitisttaservicestartdate") ||
                          MasterEntity.Attributes.Contains("dfe_gitismailinglistservicestartdate") ||
                          MasterEntity.Attributes.Contains("dfe_gitiseventsservicestartdate"))
            {
                _IsMasterSubscriber = true;
                _DmLog.IsMasterSubscriber = true;
            }
            else
            {
                _IsMasterSubscriber = false;
                _IsMasterSubscriber = false;
            }

            //master owner and type
            _DmLog.MasterOwner = ownerEr.Name;

            if (_MergeConfig.Advisors.Contains(ownerEr.Id))
            {
                _DmLog.MasterOwnerType = "Adviser";
            }
            else if (ownerEr.LogicalName.ToLower() == "team")
            {
                _DmLog.MasterOwnerType = "Team";
            }
            else
            {
                _DmLog.MasterOwnerType = "Other";
            }

            //Check if master is the latest record
            if (_DmLog.MasterRecordClosed)
            {
                SetMostRecentRecordId();
                if (_DmLog.MostRecentRecordId == MasterEntity.Id.ToString()) _DmLog.IsMasterLatest = true;
                else _DmLog.IsMasterLatest = false;
            }

        }

        public async Task ExecuteAsync(IOrganizationService Service, ILogger log)
        {
            try
            {
                service = Service;
                await SetMergeType();

                _DmLog.RecordURL = SourceRecordUrl;

                if (MergeType == MergeOption.Skip_MultipleOwners)
                {
                    _DmLog.ExecutionStatus = MergeStatus.SKIP_MultipleOwners;
                    return;
                }
                else if (MergeType == MergeOption.SKIP_MultipleOwners_Notified)
                {
                    _DmLog.ExecutionStatus = MergeStatus.SKIP_MultipleOwners_Notified;
                    return;
                }
                else if (MergeType == MergeOption.SKIP_DuplicateIsSubscriber)
                {
                    _DmLog.ExecutionStatus = MergeStatus.SKIP_DuplicateIsSubscriber;
                    return;
                }
                //   CheckITTAppliedToSkipReules();

                Entity EntityToUpdate = new Entity(MasterEntity.LogicalName, MasterEntity.Id);
                bool setTpukOwner = false;
                bool notesAdded = false;

                if (_MasterRecordClosed)
                {

                    SetMostRecentRecordId();
                    if (_DmLog.MostRecentRecordId != MasterEntity.Id.ToString())
                    {
                        _DmLog.IsMasterLatest = false;
                        // Set owner to TPUK and update Candidate status.
                        //This triggers merge plugin and creates staging record
                        setTpukOwner = SetTPUKOwner(EntityToUpdate);

                        //Check if current owner is a team, other wise plugin will faile
                        //Awaiting plugin update. Once updated follwing region can be removed
                        #region Check if current owner is TPUK


                        EntityReference ownerEr = (EntityReference)MasterEntity["ownerid"];

                        if (ownerEr.LogicalName.ToLower() == "team")
                        {
                            _DmLog.ExecutionStatus = MergeStatus.SKIP_MasterRecordClosed;
                            _DmLog.ResponseText = "Previous Owner is a Team. Awaiting plugin update";
                            return;
                        }

                        #endregion Check if current owner is TPUK
                    }
                    else _DmLog.IsMasterLatest = true;
                }
                foreach (Entity entity in DuplicateEntitieis)
                {
                    MergeResponse mergeResponse = MergeDuplicates(entity.Id);
                }

                DisableDuplicateAdivsreToDoItems();

                MergeChildEntities(MasterEntity.Id.ToString());
                _DmLog.ExecutionStatus = MergeStatus.SUCCESS;

                // append notes from duplicates
                notesAdded = AddNotes(EntityToUpdate, log);

                if (notesAdded || setTpukOwner) service.Update(EntityToUpdate);
            }
            catch (Exception ex)
            {
                _DmLog.ExecutionStatus = MergeStatus.FAIL;
                if (ex.ToString().Length > 500) _DmLog.ResponseText = ex.ToString().Substring(0, 500);
                else _DmLog.ResponseText = ex.ToString();
                _DmLog.Trace($"ERROR: {ex.ToString()}");
                return;
            }

        }

        private MergeResponse MergeDuplicates(Guid subordinateId)
        {

            MergeRequest mergeRequest = new MergeRequest();
            mergeRequest.SubordinateId = subordinateId;
            mergeRequest.Target = TargetEntityRef;
            mergeRequest.PerformParentingChecks = false;
            mergeRequest.UpdateContent = NewMasterEntity;

            MergeResponse mergeResponse = (MergeResponse)service.Execute(mergeRequest);

            return mergeResponse;
        }

        private void MergeChildEntities(string ContactID)
        {
            CandidateStepList candidateStepList = new CandidateStepList(service, ContactID);
            candidateStepList.GetCandidateSteps();
            candidateStepList.Merge();

            PhoneCallList phoneCallList = new PhoneCallList(service, ContactID);
            phoneCallList.GetPhoneCalls();
            phoneCallList.Merge();

            CandidatePrivacyPolicyList candidatePrivacyPolicyList = new CandidatePrivacyPolicyList(service, ContactID);
            candidatePrivacyPolicyList.GetCandidatePrivacyPolicies();
            candidatePrivacyPolicyList.Merge();

            EventRegistrationList eventRegList = new EventRegistrationList(service, ContactID);
            eventRegList.GetEventRegistrations();
            eventRegList.Merge();
        }

        private async Task SetMergeType()
        {

            EntityReference ownerEr = (EntityReference)MasterEntity["ownerid"];

            //Candidate Adviser Status / Open = 222750000 / Closed = 222750001
            //Candidate Status	/Assigned = 222750002

            int candidateStatusMaster = 0;
            if (MasterEntity.Attributes.Contains("dfe_candidatestatus")) candidateStatusMaster = ((OptionSetValue)MasterEntity["dfe_candidatestatus"]).Value;

            int adviserStatusMaster = 0;
            if (MasterEntity.Attributes.Contains("dfe_candidateadviserstatus")) adviserStatusMaster = ((OptionSetValue)MasterEntity["dfe_candidateadviserstatus"]).Value;

            int adviserStatusReasonMaster = 0;
            if (MasterEntity.Attributes.Contains("dfe_candidateadviserstatusreason")) adviserStatusReasonMaster = ((OptionSetValue)MasterEntity["dfe_candidateadviserstatusreason"]).Value;
            _IsMasterOwnedByAdviser = IsOwnedByAdviser(MasterEntity);
            if (_IsMasterOwnedByAdviser) _IsAnyRecordOwnedByAdvser = true;


            #region Skip Rule

            if (adviserStatusMaster == 222750001) //Closed
            {
                _MasterRecordClosed = true;
            }

            #endregion

            #region Rule2 and SES

            bool sesOverWrite = false;
            int noOfDuplicateAssignedAndOpen = 0;

            // //Check if master is already a subscriber
            // if (MasterEntity.Attributes.Contains("dfe_gitisttaservicestartdate") ||
            //               MasterEntity.Attributes.Contains("dfe_gitismailinglistservicestartdate") ||
            //               MasterEntity.Attributes.Contains("dfe_gitiseventsservicestartdate")) isMasterSubscriber = true;
            // else isMasterSubscriber = false;

            // Owned by adviser and candidatestatusmaster is Assigned and adviserstatusmaster is Open
            if (IsOwnedByAdviser(MasterEntity) && candidateStatusMaster == 222750002 && adviserStatusMaster == 222750000) noOfDuplicateAssignedAndOpen += 1;

            foreach (Entity entity in DuplicateEntitieis)
            {
                int candidateStatusDuplicate = 0;
                if (entity.Attributes.Contains("dfe_candidatestatus")) candidateStatusDuplicate = ((OptionSetValue)entity["dfe_candidatestatus"]).Value;
                int adviserStatusDuplicate = 0;
                if (entity.Attributes.Contains("dfe_candidateadviserstatus")) adviserStatusDuplicate = ((OptionSetValue)entity["dfe_candidateadviserstatus"]).Value;
                if (_IsAnyRecordOwnedByAdvser == false)
                {
                    _IsAnyRecordOwnedByAdvser = IsOwnedByAdviser(entity);
                }
                //Candidate Adviser Status / Open = 222750000 / Closed = 222750001

                if (IsOwnedByAdviser(entity) && candidateStatusDuplicate == 222750002 && adviserStatusDuplicate == 222750000) noOfDuplicateAssignedAndOpen += 1;

                if (sesOverWrite == false) sesOverWrite = SESOverwrite(entity);
                if (_IsMasterSubscriber == false)
                    //ServiceSubscriptionOverwrite(entity);

                    if (noOfDuplicateAssignedAndOpen > 1)
                    {
                        await _MergeConfig.GetMultiOwnerContactsAsync();

                        if (_MergeConfig.MultOwnersExList.Count > 0)
                        {
                            if (_MergeConfig.MultOwnersExList.Any(a => a == SourceEntity.Id.ToString().ToUpper()))
                            {
                                MergeType = MergeOption.SKIP_MultipleOwners_Notified;
                                return;
                            }
                        }

                        MergeType = MergeOption.Skip_MultipleOwners;
                        return;
                    }

                //Is Duplicate Subscriber

                if (entity.Attributes.Contains("dfe_gitisttaservicestartdate") ||
                              entity.Attributes.Contains("dfe_gitismailinglistservicestartdate") ||
                              entity.Attributes.Contains("dfe_gitiseventsservicestartdate"))
                {
                    if (_IsAnyRecordOwnedByAdvser)
                    {
                        _DmLog.IsDuplicateSubscriber = true;
                        MergeType = MergeOption.SKIP_DuplicateIsSubscriber;
                        return;
                    }
                    else ResetMasterDueToSubscription();
                }
            }

            #endregion

            #region Rule3

            if (candidateStatusMaster == 222750002)
            {
                if (adviserStatusMaster == 222750000 && IsOwnedByAdviser(MasterEntity) == true)
                {
                    MergeType = MergeOption.Merge_DoNotOverwriteMaster;
                    _DmLog.DoNotOverwriteMaster = true;
                }
            }

            #endregion

        }

        private bool SESOverwrite(Entity duplicateEntity)
        {

            //Channel Creaation SES = 222,750,021

            int masterChannelCreation = 0;
            int dupChannelCreation = 0;

            if (MasterEntity.Attributes.Contains("dfe_channelcreation")) masterChannelCreation = ((OptionSetValue)MasterEntity["dfe_channelcreation"]).Value;
            if (duplicateEntity.Attributes.Contains("dfe_channelcreation")) dupChannelCreation = ((OptionSetValue)duplicateEntity["dfe_channelcreation"]).Value;

            if (masterChannelCreation != 222750021 && dupChannelCreation == 222750021) //222750021 = SES
            {
                string[] sesAttributes = { "emailaddress2","telephone1","telephone2","dfe_hasdbscertificate","dfe_dateofissueofdbscertificate" };

                foreach (string sesAttribute in sesAttributes)
                {
                    if (duplicateEntity.Attributes.Contains(sesAttribute))
                    {
                        NewMasterEntity.Attributes.Add(sesAttribute, duplicateEntity[sesAttribute]);
                    }
                }
                return true;

            }
            return false;
        }

        private void ServiceSubscriptionOverwrite(Entity duplicateEntity)
        {
            if (_IsMasterSubscriber) return;

            string[] gitisServiceSubscriptionAttributes ={"dfe_gitisttaserviceissubscriber",
                                                        "dfe_gitisttaservicesubscriptionchannel",
                                                        "dfe_gitisttaservicestartdate",
                                                        "dfe_gitisttaservicedonotemail",
                                                        "dfe_gitisttaservicedonotbulkemail",
                                                        "dfe_gitisttaservicedonotpostalmail",
                                                        "dfe_gitisttaservicedonotbulkpostalmail",
                                                        "dfe_gitisttaservicedonotsendmm",
                                                        "dfe_gitismailinglistserviceissubscriber",
                                                        "dfe_gitismlservicesubscriptionchannel",
                                                        "dfe_gitismailinglistservicestartdate",
                                                        "dfe_gitismailinglistservicedonotemail",
                                                        "dfe_gitismailinglistservicedonotbulkemail",
                                                        "dfe_gitismailinglistservicedonotpostalmail",
                                                        "dfe_gitismlservicedonotbulkpostalmail",
                                                        "dfe_gitismailinglistservicedonotsendmm",
                                                        "dfe_gitiseventsserviceissubscriber",
                                                        "dfe_gitiseventsservicesubscriptionchannel",
                                                        "dfe_gitiseventsservicestartdate",
                                                        "dfe_gitiseventsservicedonotemail",
                                                        "dfe_gitiseventsservicedonotbulkemail",
                                                        "dfe_gitiseventsservicedonotpostalmail",
                                                        "dfe_gitiseventsservicedonotbulkpostalmail",
                                                        "dfe_gitiseventsservicedonotsendmm",
                                                        "dfe_gitiseventsservicesubscriptiontype" };


            foreach (string item in gitisServiceSubscriptionAttributes)
            {
                if (duplicateEntity.Attributes.Contains(item) && !NewMasterEntity.Attributes.Contains(item))
                {
                    NewMasterEntity.Attributes.Add(item, duplicateEntity[item]);
                }
            }
            _IsMasterSubscriber = true;

        }

        private async Task SkipAndNotify()
        {
            string bloblName;
            if (MergeType == MergeOption.Skip_MasterRecordClosed) bloblName = "ManualMerge-MasterrecordClosedforTPUKReview" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
            else if (MergeType == MergeOption.Skip_MultipleOwners) bloblName = "ManualMerge-MultipleOwnersforTPUKReview" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
            else return;

            string candicateName = "";
            string primaryEmail = "";

            if (SourceEntity.Attributes.Contains("fullname")) candicateName = SourceEntity["fullname"].ToString();
            if (SourceEntity.Attributes.Contains("emailaddress1")) primaryEmail = SourceEntity["emailaddress1"].ToString();

            string containerName = "";
            if (MergeType == MergeOption.Skip_MasterRecordClosed) containerName = "gitis-dupman-masterrecordclosedfortpukreview";
            else if (MergeType == MergeOption.Skip_MultipleOwners) containerName = "gitis-dupman-multipleownersfortpukreview";
            else return;

            string recordId = SourceEntity.Id.ToString();
            string dtStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");


            CloudBlobClient client;
            CloudBlobContainer container;

            string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            client = storageAccount.CreateCloudBlobClient();
            container = client.GetContainerReference(containerName);
            await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

            CloudAppendBlob blob;
            blob = container.GetAppendBlobReference(bloblName);

            string logText = "";
            bool blobExists = await blob.ExistsAsync();

            if (blobExists) //append to existing blob
            {
                blob = container.GetAppendBlobReference(bloblName);
                logText += Environment.NewLine;
                logText += recordId + "," + candicateName + "," + primaryEmail + "," + SourceRecordUrl + "," + dtStamp;
                byte[] byteArray = Encoding.ASCII.GetBytes(logText);
                MemoryStream stream = new MemoryStream(byteArray);
                await blob.AppendFromStreamAsync(stream);
            }
            else //create new blob
            {
                logText = "RecordId,CandidateName, PrimaryEmail, CRMURL, ExecutionDateTime";
                logText += Environment.NewLine;
                logText += recordId + "," + candicateName + "," + primaryEmail + "," + SourceRecordUrl + "," + dtStamp;
                byte[] byteArray = Encoding.ASCII.GetBytes(logText);
                MemoryStream stream = new MemoryStream(byteArray);
                await blob.UploadFromStreamAsync(stream);
            }

        }

        private bool IsOwnedByAdviser(Entity entity)
        {
            string adviserName = ((EntityReference)entity["ownerid"]).Name;
            if (adviserName.ToLower().Contains("naim") || adviserName.ToLower().Contains("arjun")) return true;
            Guid ownerId = ((EntityReference)entity["ownerid"]).Id;
            if (_MergeConfig.Advisors.Contains(ownerId)) return true;
            else return false;

        }

        private void SetMostRecentRecordId()
        {
            DateTime dupCreatedOn = new DateTime();
            DateTime masterCreatedOn = (DateTime)MasterEntity["createdon"];
            _DmLog.MostRecentRecordId = MasterEntity.Id.ToString();

            foreach (Entity entity in DuplicateEntitieis)
            {
                if (dupCreatedOn == DateTime.MinValue)
                {
                    dupCreatedOn = (DateTime)entity["createdon"];
                    if (dupCreatedOn > masterCreatedOn) _DmLog.MostRecentRecordId = entity.Id.ToString();
                }
                else
                {
                    if ((DateTime)entity["createdon"] > dupCreatedOn)
                    {
                        dupCreatedOn = (DateTime)entity["createdon"];
                        if (dupCreatedOn > masterCreatedOn) _DmLog.MostRecentRecordId = entity.Id.ToString();
                    }
                }


            }
        }

        private bool AddNotes(Entity EntityToUpdate, ILogger log)
        {
            List<CandidateNotes> DupNoteList = new List<CandidateNotes>();

            string notesMiscMaster = null;
            string notesForContactDetailsMaster = null;
            string notesForSecurityMaster = null;
            string notesForClassRoomExperience = null;

            if (MasterEntity.Contains("dfe_notesforcontactdetails")) notesForContactDetailsMaster = MasterEntity["dfe_notesforcontactdetails"].ToString();
            if (MasterEntity.Contains("dfe_notesforsecurity")) notesForSecurityMaster = MasterEntity["dfe_notesforsecurity"].ToString();
            if (MasterEntity.Contains("dfe_notesmisc")) notesMiscMaster = MasterEntity["dfe_notesmisc"].ToString();
            if (MasterEntity.Contains("dfe_notesforclassroomexperience")) notesForClassRoomExperience = MasterEntity["dfe_notesforclassroomexperience"].ToString();

            foreach (Entity entity in DuplicateEntitieis)
            {
                CandidateNotes canNotes = new CandidateNotes(entity.Id);
                canNotes.CreatedOn = (DateTime)entity["createdon"];

                if (entity.Contains("dfe_notesforcontactdetails")) canNotes.NotesForContactDetails = entity["dfe_notesforcontactdetails"].ToString();
                if (entity.Contains("dfe_notesforsecurity")) canNotes.NotesForSecurity = entity["dfe_notesforsecurity"].ToString();
                if (entity.Contains("dfe_notesmisc")) canNotes.NotesMisc = entity["dfe_notesmisc"].ToString();
                if (entity.Contains("dfe_notesforclassroomexperience")) canNotes.NotesForSchoolExperience = entity["dfe_notesforclassroomexperience"].ToString();


                if (
                    canNotes.NotesForContactDetails != null || 
                    canNotes.NotesForSecurity != null || 
                    canNotes.NotesMisc != null ||
                    canNotes.NotesForSchoolExperience != null)
                {
                    DupNoteList.Add(canNotes);
                }
            }

            if (DupNoteList.Count == 0) return false;

            var sortedNoteList = DupNoteList.OrderByDescending(o => o.CreatedOn);

            foreach (var item in sortedNoteList)
            {
                if (item.NotesForContactDetails != null)
                {

                    notesForContactDetailsMaster += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesForContactDetails;
                }

                if (item.NotesForSecurity != null)
                {
                    notesForSecurityMaster += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesForSecurity;
                }

                if (item.NotesMisc != null)
                {
                    notesMiscMaster += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesMisc;
                }


                if (item.NotesForSchoolExperience != null)
                {
                    notesForClassRoomExperience += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesForSchoolExperience;
                }
            }

            if (!EntityToUpdate.Attributes.Contains("dfe_notesforcontactdetails"))
                EntityToUpdate.Attributes.Add("dfe_notesforcontactdetails", notesForContactDetailsMaster);
            else EntityToUpdate["dfe_notesforcontactdetails"] = notesForContactDetailsMaster;

            if (!EntityToUpdate.Attributes.Contains("dfe_notesforsecurity"))
                EntityToUpdate.Attributes.Add("dfe_notesforsecurity", notesForSecurityMaster);
            else EntityToUpdate["dfe_notesforsecurity"] = notesForSecurityMaster;

            if (!EntityToUpdate.Attributes.Contains("dfe_notesmisc"))
                EntityToUpdate.Attributes.Add("dfe_notesmisc", notesMiscMaster);
            else EntityToUpdate["dfe_notesmisc"] = notesMiscMaster;

            if (!EntityToUpdate.Attributes.Contains("dfe_notesforclassroomexperience"))
                EntityToUpdate.Attributes.Add("dfe_notesforclassroomexperience", notesForClassRoomExperience);
            else 
                EntityToUpdate["dfe_notesforclassroomexperience"] = notesForClassRoomExperience;


            return true;

        }

        private void CheckITTAppliedToSkipReules()
        // Added this rule as a temporary mesaure to avoid merge issues as per Jira ticket 1907
        //Changes applied on 17/09/2020
        {
            foreach (var item in _DmRetrieveResponse.DuplicateEntities)
            {
                if (item.Attributes.Contains("dfe_datereleased"))
                {
                    MergeType = MergeOption.Skip_ITTAppliedTo;
                    _DmLog.ExecutionStatus = MergeStatus.SKIP_ITTAppliedTo;
                    _DmLog.ResponseText = "Duplicate [" + item.Id.ToString() + "] has Release Dates";
                    return;
                }
            }

            if (_DmRetrieveResponse.ITTAppliedToList.Count > 0)
            {
                MergeType = MergeOption.Skip_ITTAppliedTo;
                _DmLog.ExecutionStatus = MergeStatus.SKIP_ITTAppliedTo;
                _DmLog.ResponseText = "ITT Applied on Duplicate [" + _DmRetrieveResponse.ITTAppliedToList[0].ContactId.ToString() + "] has Accepted and/or Applied dates";
            }
        }
        private bool SetTPUKOwner(Entity EntityToUpdate)
        {

            bool isMasterOwnedByAdviser = IsOwnedByAdviser(MasterEntity);

            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                  <entity name='team'>
                                    <attribute name='name' />
                                    <attribute name='teamid' />
                                    <attribute name='teamtype' />
                                    <order attribute='name' descending='false' />
                                    <filter type='and'>
                                      <condition attribute='name' operator='eq' value='TPUK' />
                                    </filter>
                                  </entity>
                                </fetch>";

            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));

            if (eCol == null || eCol.Entities.Count == 0) return false;



            EntityReference tpukTeamEr = new EntityReference(eCol.Entities[0].LogicalName, eCol.Entities[0].Id);

            if (EntityToUpdate.Attributes.Contains("ownerid")) EntityToUpdate["ownerid"] = tpukTeamEr;
            else EntityToUpdate.Attributes.Add("ownerid", tpukTeamEr);

            //Set Candidate Adviser status to open
            if (EntityToUpdate.Attributes.Contains("dfe_candidateadviserstatus"))
                EntityToUpdate["dfe_candidateadviserstatus"] = new OptionSetValue(222750000);
            else EntityToUpdate.Attributes.Add("dfe_candidateadviserstatus", new OptionSetValue(222750000));

            //Set Candidate Adviser Status Reason to Null/Clear value
            if (EntityToUpdate.Attributes.Contains("dfe_candidateadviserstatusreason"))
                EntityToUpdate["dfe_candidateadviserstatusreason"] = null;
            else EntityToUpdate.Attributes.Add("dfe_candidateadviserstatusreason", null);

            //Set Candidate Status to Previously Assigned Candidate
            if (EntityToUpdate.Attributes.Contains("dfe_candidatestatus"))
                EntityToUpdate["dfe_candidatestatus"] = new OptionSetValue(222750003);
            else EntityToUpdate.Attributes.Add("dfe_candidatestatus", new OptionSetValue(222750003));

            return true;
        }

        private void DisableDuplicateAdivsreToDoItems()
        {
            string fetchString = @"<fetch version='1.0' output-format='xml-platform' mapping='logical' distinct='false'>
                                  <entity name='dfe_advisertodoitem'>
                                    <attribute name='dfe_advisertodoitemid' />
                                    <attribute name='dfe_name' />
                                    <attribute name='createdon' />
                                    <attribute name='statuscode' />
                                    <attribute name='dfe_status' />
                                    <attribute name='statecode' />
                                    <attribute name='modifiedon' />
                                    <attribute name='modifiedby' />
                                    <attribute name='dfe_duedate' />
                                    <attribute name='dfe_regardingcandidate' />
                                    <order attribute='dfe_name' descending='false' />
                                    <filter type='and'>
                                      <condition attribute='dfe_status' operator='eq' value='222750000' />
                                      <condition attribute='statecode' operator='eq' value='0' />
                                      <condition attribute='modifiedon' operator='today' />
                                      <condition attribute='dfe_regardingcandidate' operator='eq' uitype='contact' value='{" + MasterEntity.Id + @"}' />
                                    </filter>
                                  </entity>
                                </fetch>";


            EntityCollection eCol = service.RetrieveMultiple(new FetchExpression(fetchString));

            if (eCol == null || eCol.Entities.Count == 0) return;


            ExecuteMultipleRequest multiReq = new ExecuteMultipleRequest();
            multiReq.Settings = new ExecuteMultipleSettings()
            {
                ContinueOnError = true,
                ReturnResponses = false
            };
            multiReq.Requests = new OrganizationRequestCollection();

            foreach (var entity in eCol.Entities)
            {
                string modifiedByName = ((EntityReference)entity["modifiedby"]).Name;
                if (modifiedByName.Contains("LOG-API") == false) continue;


                DateTime modifiedOn = (DateTime)entity["modifiedon"];
                string shortDTNow = DateTime.Now.ToShortDateString();

                DateTime dueDate = new DateTime();

                if (entity.Attributes.Contains("dfe_duedate")) dueDate = (DateTime)entity["dfe_duedate"];
                if (dueDate > DateTime.Now) continue;


                if (modifiedOn.ToShortDateString() != DateTime.Now.ToShortDateString()) continue;
                if (modifiedOn.ToShortDateString() != DateTime.Now.ToShortDateString()) continue;

                Entity updateEntity = new Entity(entity.LogicalName, entity.Id);

                updateEntity.Attributes.Add("statecode", new OptionSetValue(1));
                updateEntity.Attributes.Add("statuscode", new OptionSetValue(2));

                UpdateRequest upReq = new UpdateRequest();
                upReq.Target = updateEntity;
                multiReq.Requests.Add(upReq);
            }


            service.Execute(multiReq);

        }
        private void ResetMasterDueToSubscription()
        {
            DateTime masterCreatedOn = (DateTime)MasterEntity["createdon"];
            DateTime dupCreatedOn = new DateTime();
            DateTime oldestCreatedOnDate = new DateTime();



            //Check if master is already a subscriber
            if (MasterEntity.Attributes.Contains("dfe_gitisttaservicestartdate") ||
                          MasterEntity.Attributes.Contains("dfe_gitismailinglistservicestartdate") ||
                          MasterEntity.Attributes.Contains("dfe_gitiseventsservicestartdate"))
            {
                _IsMasterSubscriber = true;
                oldestCreatedOnDate = (DateTime)MasterEntity["createdon"];
            }

            foreach (var item in DuplicateEntitieis)
            {
                bool isDupSubscriber = false;
                DateTime createdOn = new DateTime();

                if (item.Attributes.Contains("dfe_gitisttaservicestartdate") ||
                         item.Attributes.Contains("dfe_gitismailinglistservicestartdate") ||
                         item.Attributes.Contains("dfe_gitiseventsservicestartdate"))
                {
                    isDupSubscriber = true;
                    createdOn = (DateTime)item["createdon"];
                }

                if (createdOn != DateTime.MinValue && createdOn.Date < masterCreatedOn)
                {

                }
            }

        }
    }

    public class CandidateNotes
    {
        public Guid ContactID { get; set; }
        public DateTime CreatedOn { get; set; }
        public string NotesMisc { get; set; }
        public string NotesForContactDetails { get; set; }
        public string NotesForSecurity { get; set; }
        public string NotesForSchoolExperience { get; set; }

        public CandidateNotes(Guid contactId)
        {
            ContactID = contactId;
        }
    }

    public enum ExtraFieldHandlingOperationEnum { Add = 1, Overwrite=2 }
    public class ExtraFieldHandling
    {
        public string TargetField { get; set; }
        public ExtraFieldHandlingOperationEnum? Operation { get; set; }
        public string OtherField { get; set; }
    }

    public class ManualMergeRequest
    {
        public DMEntity Master { get; set; }
        public List<DMEntity> DupList { get; set; }

        [JsonIgnore]
        IOrganizationService service;
        [JsonIgnore]
        public int totalMerged = 0;
        [JsonIgnore]
        public int failed = 0;
        [JsonIgnore]
        List<string> exMultiOwnerList = new List<string>();

        [JsonIgnore]
        Entity MasterEntity = null;

        [JsonIgnore]
        Entity NewMasterEntity = null;

        [JsonIgnore]
        List<Entity> DuplicateEntities = new List<Entity>();

        public async Task<string> ExecuteAsync(
            IOrganizationService Service,
            ILogger log)
        {
            string response = "";
            service = Service;
            MergeConfig mConfig = new MergeConfig();
            mConfig.GetData(service, log);

            NewMasterEntity = new Entity("contact");
            string contactId = Master.Id.ToString();

            foreach (DMAttribute attribute in Master.DMAttributes)
            {
                log.LogInformation($"Processing Attribute {attribute?.Name}");
                
                if (attribute.IsReadOnly || !attribute.IsDirty) continue;
                if (attribute.Value == null)
                {
                    NewMasterEntity.Attributes.Add(attribute.Name, null);
                    continue;
                }

                FillEntityWithAttributes(attribute, NewMasterEntity, mConfig, log);

            }


            EntityReference targetEntityRef = new EntityReference(NewMasterEntity.LogicalName, Master.Id);

            //log.LogInformation("Retrieving Duplicates");

            GetDuplicateEntities(log);

            log.LogInformation($"DupList Count is {DupList?.Count}");
            if (DupList.Count > 0)
            {
                foreach (DMEntity dme in DupList)
                {
                    log.LogInformation($"Processing entity {dme?.Id} from DupList. Include in merge {dme?.IncludeInMarge}.");
                    if (dme.IncludeInMarge)
                    {
                        try
                        {
                            AmendNewMasterEntityWithApplyFieldsIfSubordinateIsApplyRecord(NewMasterEntity, dme.Id, mConfig, log);

                            MergeResponse mergeResponse = MergeDuplicates(NewMasterEntity, targetEntityRef, dme.Id);
                            totalMerged += 1;
                        }
                        catch (Exception ex)
                        {
                            log.LogError($"Exception\n {ex?.Message}");
                            log.LogError($"Inner Exception\n {ex?.InnerException?.Message}");
                            string x = ex.ToString();
                            failed += 1;
                        }
                    }
                }
            }
            if (totalMerged > 0) MergeChildEntities(contactId);
            response = "Records merged [" + totalMerged + "], " + "failed [" + failed + "]";

            #region readBlob
            string blobContent = "";
            bool listIsDirty = false;

            //check and remove ids where email sent to tpuk for multiple owner exceptions
            MergeConfig mergeConfig = new MergeConfig();
            await mergeConfig.GetMultiOwnerContactsAsync();

            if (mergeConfig.MultOwnersExList.Contains(contactId.ToUpper()))
            {
                mergeConfig.MultOwnersExList.RemoveAll(r => r == contactId.ToUpper());
                listIsDirty = true;
            }

            foreach (DMEntity dme in DupList)
            {
                string dupContactId = dme.Id.ToString();
                if (mergeConfig.MultOwnersExList.Contains(dupContactId.ToUpper()))
                {
                    mergeConfig.MultOwnersExList.Remove(dupContactId);
                    listIsDirty = true;
                }
            }

            if (listIsDirty)
            {
                foreach (var item in mergeConfig.MultOwnersExList)
                {
                    blobContent += Environment.NewLine + item;
                }

                await UpdateBlobMultipleOwnerAsync(blobContent);
            }

            #endregion


            // append notes from duplicates
            Entity masterEntityUpdateJustForNotes = new Entity(NewMasterEntity.LogicalName,Guid.Parse(contactId));
            bool notesAdded = AddNotes(masterEntityUpdateJustForNotes, log);
            if (notesAdded)
            {
                log.LogInformation("Making the notes update to Entity {0}", masterEntityUpdateJustForNotes.Id);
                try
                {
                    service.Update(masterEntityUpdateJustForNotes);
                } catch(Exception exc)
                {
                    log.LogError("Error in Notes Update: {0}", exc.Message);
                    log.LogError(exc.StackTrace);
                }
            }

            return response;
        }

        private void FillEntityWithAttributes(
            DMAttribute attribute, 
            Entity masterEntity,
            MergeConfig mConfig,
            ILogger log)
        {
            string attributeField = attribute.Name;

            if (CheckIfAttributeToBeAddedToDifferentField(mConfig, attribute.Name, out string otherField, log))
            {
                attributeField = otherField;
            }
           
            if (attribute.Value != null)
            {

                if (masterEntity.Contains(attribute.Name))
                {
                    masterEntity[attributeField] = GetValueOfAttribute(attribute);
                }
                else
                {
                    // If the Master Entity field is empty 
                    // then we add it to the original attribute
                    masterEntity.Attributes.Add(attribute.Name, GetValueOfAttribute(attribute));
                }

            } else
            {
                // If the attribute value is null and the master entity contains a value
                // then remove the attribute from the Master Entity.
                if (masterEntity.Contains(attributeField))
                {
                    masterEntity.Attributes.Remove(attributeField);
                }
            }
        }

        private bool CheckIfAttributeToBeAddedToDifferentField(MergeConfig mConfig, string name, out string otherField, ILogger log)
        {
            otherField = null;

            if (!string.IsNullOrEmpty(name) 
                && mConfig.ExtraFieldHandlingList != null && mConfig.ExtraFieldHandlingList.Count > 0)
            {
                var foundExtraFieldHandling = mConfig.ExtraFieldHandlingList.Where(x => x.TargetField == name).FirstOrDefault();
                if (foundExtraFieldHandling != null)
                {

                    if (foundExtraFieldHandling.Operation == ExtraFieldHandlingOperationEnum.Add)
                    {
                        log.LogInformation("{0} attribute is in Add operation. Adding value to '{1}'", name, foundExtraFieldHandling.OtherField);
                        otherField = foundExtraFieldHandling.OtherField;
                        return true;
                    }
                }
            }

            return false;
        }

        private object GetValueOfAttribute(DMAttribute attribute)
        {
            if (attribute.Value == null)
            {
                return null;
            }
            else
            {
                if (attribute.Type == DMAttributeType.Picklist)
                {
                    return new OptionSetValue(int.Parse(attribute.Value.ToString()));
                }
                else if (attribute.Type == DMAttributeType.String)
                {
                    return attribute.Value.ToString();
                }
                else if (attribute.Type == DMAttributeType.Memo)
                {
                    return attribute.Value.ToString();
                }
                else if (attribute.Type == DMAttributeType.Lookup)
                {
                    EntityReference er = new EntityReference(attribute.Lookuplogicalname, new Guid(attribute.Value.ToString()));
                    return er;
                }
                else if (attribute.Type == DMAttributeType.Boolean)
                {
                    return (bool)attribute.Value;
                }
                else if (attribute.Type == DMAttributeType.Integer)
                {
                    return (int)attribute.Value;
                }
                else if (attribute.Type == DMAttributeType.Owner)
                {
                    EntityReference er = new EntityReference(attribute.Lookuplogicalname, new Guid(attribute.Value.ToString()));
                    return er;
                }
                else if (attribute.Type == DMAttributeType.DateTime)
                {
                    if (attribute.FormattedValue != null)
                    {
                        if (attribute.FormattedValue != "")
                        {
                            int yy = int.Parse(attribute.FormattedValue.Substring(6, 4));
                            int mm = int.Parse(attribute.FormattedValue.Substring(3, 2));
                            int dd = int.Parse(attribute.FormattedValue.Substring(0, 2));
                            DateTime dt = new DateTime(yy, mm, dd);

                            return dt;
                        }
                    }
                }
            }

            return null;
        }

        private void AmendNewMasterEntityWithApplyFieldsIfSubordinateIsApplyRecord(
            Entity newMasterEntity, 
            Guid subordinateId, 
            MergeConfig mConfig,
            ILogger logger)
        {
            Entity subOrdinateEntity = null;
            // Checking if this is an apply record
            if (IsAnApplyRecord(subordinateId, out subOrdinateEntity, logger))
            {
                ShouldAddOrOverrideApplyFields(newMasterEntity, subOrdinateEntity, mConfig, logger);
            }
        }

        private void ShouldAddOrOverrideApplyFields(
            Entity newMasterEntity, 
            Entity subOrdinateEntity, 
            MergeConfig mConfig,
            ILogger logger)
        {
            // Get all the fields from the merge config
            if (mConfig.RequiredApplyAttributes?.Count > 0)
            {
                logger.LogInformation("We have {0} apply attributes in the config", mConfig.RequiredApplyAttributes.Count);

                List<DMAttribute> applyFields = new List<DMAttribute>();
                foreach (var attr in mConfig.RequiredApplyAttributes)
                {
                    DMAttribute applyAttribute = Helper.CloneDmAttribute(attr);

                    applyAttribute.Value = null;
                    applyAttribute.FormattedValue = null;

                    if (subOrdinateEntity.Attributes.ContainsKey(attr.Name))
                    {
                        logger.LogInformation("Processing Apply Attribute {0}", attr.Name);

                        var attribute = subOrdinateEntity.Attributes.FirstOrDefault(f => f.Key == attr.Name);
                        logger.LogInformation("Attr Type is {0}", attr.Type);
                        
                        Helper.ApplyValueToDmAttribute(attr.Type, applyAttribute, subOrdinateEntity, null, attribute);
                    }

                    applyFields.Add(applyAttribute);
                }

                if (applyFields.Count > 0)
                {
                    foreach (DMAttribute dMAttribute in applyFields)
                    {
                        FillEntityWithAttributes(dMAttribute, NewMasterEntity, mConfig, logger);
                    }
                }
            }
            
            // if the field has a value then update the newMasterEntityWith that DmAttribute Field
        }

        

        private bool IsAnApplyRecord(Guid subordinateId, out Entity subOrdinateEntity, ILogger logger)
        {
            logger.LogInformation("Checking to see if this is an apply record.");

            subOrdinateEntity = service.Retrieve("contact", subordinateId, new ColumnSet(true));
            List<string> applyFiedsToCheck = new List<string>();
            List<bool> applyConditionResults = new List<bool>();

            if (subOrdinateEntity != null)
            {
                applyConditionResults.Add(subOrdinateEntity.Contains("dfe_applycreatedon")
                    && subOrdinateEntity.GetAttributeValue<DateTime>("dfe_applycreatedon") != DateTime.MinValue);
                applyConditionResults.Add(subOrdinateEntity.Contains("dfe_applyid") 
                    && !string.IsNullOrEmpty(subOrdinateEntity.GetAttributeValue<string>("dfe_applyid")));
            }

            if (applyConditionResults.Any(x => x))
            {
                logger.LogInformation("Contact with id {0} IS any apply record!", subordinateId);
            } else
            {
                subOrdinateEntity=null;
                logger.LogInformation("Contact with id {0} IS NOT any apply record!", subordinateId);
            }

            return  applyConditionResults.Any(x => x);
        }

        private MergeResponse MergeDuplicates(Entity masterEntity, EntityReference targetEntityReference, Guid subordinateId)
        {


            MergeRequest mergeRequest = new MergeRequest();
            mergeRequest.SubordinateId = subordinateId;
            mergeRequest.Target = targetEntityReference;
            mergeRequest.PerformParentingChecks = false;
            mergeRequest.UpdateContent = masterEntity;

            MergeResponse mergeResponse = (MergeResponse)service.Execute(mergeRequest);

            return mergeResponse;
        }

        private void MergeChildEntities(string ContactID)
        {
            CandidateStepList candidateStepList = new CandidateStepList(service, ContactID);
            candidateStepList.GetCandidateSteps();
            candidateStepList.Merge();

            PhoneCallList phoneCallList = new PhoneCallList(service, ContactID);
            phoneCallList.GetPhoneCalls();
            phoneCallList.Merge();

            CandidatePrivacyPolicyList candidatePrivacyPolicyList = new CandidatePrivacyPolicyList(service, ContactID);
            candidatePrivacyPolicyList.GetCandidatePrivacyPolicies();
            candidatePrivacyPolicyList.Merge();

            EventRegistrationList eventRegList = new EventRegistrationList(service, ContactID);
            eventRegList.GetEventRegistrations();
            eventRegList.Merge();
        }

        private async Task UpdateBlobMultipleOwnerAsync(string content)
        {
            try
            {
                CloudBlobClient client;
                CloudBlobContainer container;

                string storageConnectionString = Environment.GetEnvironmentVariable("AzureStorageQueue");
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
                client = storageAccount.CreateCloudBlobClient();
                container = client.GetContainerReference("gitis-dupman-emailapprovalsent-tpuk");
                await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Container, new BlobRequestOptions(), new OperationContext());

                string bloblName;
                CloudBlockBlob blob;

                bloblName = "MultipleOwnersEmailSentToTPUK.csv";
                blob = container.GetBlockBlobReference(bloblName);

                bool blobExists = await blob.ExistsAsync();

                if (blobExists) //appent to existing blob
                {
                    await blob.UploadTextAsync(content);
                }
            }
            catch (Exception)
            {

            }
        }

        private void GetDuplicateEntities(ILogger log)
        {
            MasterEntity = service.Retrieve("contact", Master.Id, new ColumnSet(true));
            DuplicateEntities.Clear();

            log.LogInformation($"GetDuplicatedEntities MasterId is {Master?.Id}");
            bool sesOverWrite = false;

            RetrieveDuplicatesRequest dupRequest = new RetrieveDuplicatesRequest();
            dupRequest.BusinessEntity = MasterEntity;
            dupRequest.MatchingEntityName = MasterEntity.LogicalName;
            dupRequest.PagingInfo = new PagingInfo() { PageNumber = 1, Count = 50 };

            RetrieveDuplicatesResponse retrieveDuplicateResponse = (RetrieveDuplicatesResponse)service.Execute(dupRequest);

            log.LogInformation($"Retrieve Duplicate Response Number of Entities is {retrieveDuplicateResponse?.DuplicateCollection?.Entities?.Count}");

            if (retrieveDuplicateResponse.DuplicateCollection.Entities != null &&
                retrieveDuplicateResponse.DuplicateCollection.Entities.Count > 0)
            {
                foreach (Entity item in retrieveDuplicateResponse.DuplicateCollection.Entities)
                {
                    log.LogInformation($"Adding {item?.Id} item to DuplicateEntities.");
                    DuplicateEntities.Add(item);
                    if (sesOverWrite == false) sesOverWrite = SESOverwrite(item);
                }

            }
        }

        private bool SESOverwrite(Entity duplicateEntity)
        {

            //Channel Creaation SES = 222,750,021

            int masterChannelCreation = 0;
            int dupChannelCreation = 0;

            if (MasterEntity.Attributes.Contains("dfe_channelcreation")) masterChannelCreation = ((OptionSetValue)MasterEntity["dfe_channelcreation"]).Value;
            if (duplicateEntity.Attributes.Contains("dfe_channelcreation")) dupChannelCreation = ((OptionSetValue)duplicateEntity["dfe_channelcreation"]).Value;

            if (masterChannelCreation != 222750021 && dupChannelCreation == 222750021) //222750021 = SES
            {
                string[] sesAttributes = { "emailaddress2", "telephone1", "telephone2", "dfe_notesforclassroomexperience", "dfe_hasdbscertificate", "dfe_dateofissueofdbscertificate" };

                foreach (string sesAttribute in sesAttributes)
                {
                    if (duplicateEntity.Attributes.Contains(sesAttribute))
                    {
                        NewMasterEntity.Attributes.Add(sesAttribute, duplicateEntity[sesAttribute]);
                    }
                }
                return true;

            }
            return false;
        }

        private bool AddNotes(Entity entityToAddNotesTo, ILogger log)
        {
            log.LogInformation("Entering Add Notes functionality");
            List<CandidateNotes> DupNoteList = new List<CandidateNotes>();

            string notesMiscMaster = null;
            string notesForContactDetailsMaster = null;
            string notesForSecurityMaster = null;
            string notesForClassRoomExperience = null;

            if (MasterEntity.Contains("dfe_notesforcontactdetails")) notesForContactDetailsMaster = MasterEntity["dfe_notesforcontactdetails"].ToString();
            if (MasterEntity.Contains("dfe_notesforsecurity")) notesForSecurityMaster = MasterEntity["dfe_notesforsecurity"].ToString();
            if (MasterEntity.Contains("dfe_notesmisc")) notesMiscMaster = MasterEntity["dfe_notesmisc"].ToString();
            if (MasterEntity.Contains("dfe_notesforclassroomexperience")) notesForClassRoomExperience = MasterEntity["dfe_notesforclassroomexperience"].ToString();


            if (DupList != null)
            {
                log.LogInformation("In notes we have a Duplicate list");
                foreach (DMEntity dmEntity in DupList)
                {
                    log.LogInformation("Processing Add Notes for entity Id: {0}", dmEntity.Id);
                    CandidateNotes canNotes = new CandidateNotes(dmEntity.Id);

                    Entity entity = service.Retrieve("contact", dmEntity.Id, new ColumnSet(true));
                    
                    canNotes.CreatedOn = (DateTime)entity["createdon"];

                    if (entity.Contains("dfe_notesforcontactdetails")) canNotes.NotesForContactDetails = entity["dfe_notesforcontactdetails"].ToString();
                    if (entity.Contains("dfe_notesforsecurity")) canNotes.NotesForSecurity = entity["dfe_notesforsecurity"].ToString();
                    if (entity.Contains("dfe_notesmisc")) canNotes.NotesMisc = entity["dfe_notesmisc"].ToString();
                    if (entity.Contains("dfe_notesforclassroomexperience")) canNotes.NotesForSchoolExperience = entity["dfe_notesforclassroomexperience"].ToString();

                    if (
                        canNotes.NotesForContactDetails != null || 
                        canNotes.NotesForSecurity != null || 
                        canNotes.NotesMisc != null ||
                        canNotes.NotesForSchoolExperience != null)
                    {
                        DupNoteList.Add(canNotes);
                    }
                }
            }

            if (DupNoteList.Count == 0) return false;

            var sortedNoteList = DupNoteList.OrderByDescending(o => o.CreatedOn);

            foreach (var item in sortedNoteList)
            {
                if (item.NotesForContactDetails != null)
                {
                    log.LogInformation("Adding NotesForContactDetails");

                    notesForContactDetailsMaster += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesForContactDetails;
                }

                if (item.NotesForSecurity != null)
                {
                    log.LogInformation("Adding NotesForSecurity");
                    notesForSecurityMaster += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesForSecurity;
                }

                if (item.NotesMisc != null)
                {
                    log.LogInformation("Adding NotesMisc From Id {0}", item.ContactID);
                    notesMiscMaster += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesMisc;
                }

                if (item.NotesForSchoolExperience != null)
                {
                    notesForClassRoomExperience += Environment.NewLine + Environment.NewLine

                        + "Notes from duplicate [" + item.ContactID.ToString() + "] created on " + item.CreatedOn.ToString("dd/MM/yyyy")
                        + "----------------------------------------------------------------------------------------" + Environment.NewLine
                        + item.NotesForSchoolExperience;
                }
            }

            if (!entityToAddNotesTo.Attributes.Contains("dfe_notesforcontactdetails"))
                entityToAddNotesTo.Attributes.Add("dfe_notesforcontactdetails", notesForContactDetailsMaster);
            else entityToAddNotesTo["dfe_notesforcontactdetails"] = notesForContactDetailsMaster;

            if (!entityToAddNotesTo.Attributes.Contains("dfe_notesforsecurity"))
                entityToAddNotesTo.Attributes.Add("dfe_notesforsecurity", notesForSecurityMaster);
            else entityToAddNotesTo["dfe_notesforsecurity"] = notesForSecurityMaster;

            if (!entityToAddNotesTo.Attributes.Contains("dfe_notesmisc"))
                entityToAddNotesTo.Attributes.Add("dfe_notesmisc", notesMiscMaster);
            else entityToAddNotesTo["dfe_notesmisc"] = notesForSecurityMaster;

            if (!entityToAddNotesTo.Attributes.Contains("dfe_notesforclassroomexperience"))
                entityToAddNotesTo.Attributes.Add("dfe_notesforclassroomexperience", notesForClassRoomExperience);
            else
                entityToAddNotesTo["dfe_notesforclassroomexperience"] = notesForClassRoomExperience;

            return true;
        }
    }

}
