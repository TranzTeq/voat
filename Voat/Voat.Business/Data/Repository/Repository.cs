﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Web.Mvc;
using Voat.Domain.Query;
using Voat.Common;
using Voat.Data.Models;
using Voat.Models;
using Voat.Domain.Models;
using Voat.Rules;
using Voat.RulesEngine;
using Voat.Utilities;
using Voat.Utilities.Components;
using Voat.Domain.Command;
using System.Text.RegularExpressions;
using Voat.Domain;
using System.Data.Entity;
using Voat.Caching;
using Dapper;
using Voat.Configuration;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.EntityFramework;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Voat.Data
{
    public partial class Repository : IDisposable
    {
        private static LockStore _lockStore = new LockStore();
        private voatEntities _db;

        #region Class
        public Repository() : this(new voatEntities())
        {
            /*no-op*/
        }

        public Repository(Models.voatEntities dbContext)
        {
            _db = dbContext;

            //Prevent EF from creating dynamic proxies, those mother fathers. This killed
            //us during The Fattening, so we throw now -> (╯°□°)╯︵ ┻━┻
            _db.Configuration.ProxyCreationEnabled = false;
        }
        public void Dispose()
        {
            Dispose(false);
        }

        ~Repository()
        {
            Dispose(true);
        }

        protected void Dispose(bool gcCalling)
        {
            if (_db != null)
            {
                _db.Dispose();
            }
            if (!gcCalling)
            {
                System.GC.SuppressFinalize(this);
            }
        }
        #endregion  

        #region Vote
        [Authorize]
        public VoteResponse VoteComment(int commentID, int vote, string addressHash, bool revokeOnRevote = true)
        {
            DemandAuthentication();

            //make sure we don't have bad int values for vote
            if (Math.Abs(vote) > 1)
            {
                throw new ArgumentOutOfRangeException("vote", "Valid values for vote are only: -1, 0, 1");
            }

            string userName = User.Identity.Name;
            var ruleContext = new VoatRuleContext();
            ruleContext.PropertyBag.AddressHash = addressHash;
            RuleOutcome outcome = null;

            string REVOKE_MSG = "Vote has been revoked";

            var synclock_comment = _lockStore.GetLockObject(String.Format("comment:{0}", commentID));
            lock (synclock_comment)
            {
                var comment = _db.Comments.FirstOrDefault(x => x.ID == commentID);

                if (comment != null)
                {
                    if (comment.IsDeleted)
                    {
                        return VoteResponse.Ignored(0, "Deleted comments cannot be voted");

                        //throw new VoatValidationException("Deleted comments cannot be voted");
                    }

                    //ignore votes if user owns it
                    if (String.Equals(comment.UserName, userName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return VoteResponse.Ignored(0, "User is prevented from voting on own content");
                    }

                    //check existing vote
                    int existingVote = 0;
                    var existingVoteTracker = _db.CommentVoteTrackers.FirstOrDefault(x => x.CommentID == commentID && x.UserName == userName);
                    if (existingVoteTracker != null)
                    {
                        existingVote = existingVoteTracker.VoteStatus;
                    }

                    // do not execute voting, user has already up/down voted item and is submitting a vote that matches their existing vote
                    if (existingVote == vote && !revokeOnRevote)
                    {
                        return VoteResponse.Ignored(existingVote, "User has already voted this way.");
                    }

                    //set properties for rules engine
                    ruleContext.CommentID = commentID;
                    ruleContext.SubmissionID = comment.SubmissionID;
                    ruleContext.PropertyBag.CurrentVoteValue = existingVote; //set existing vote value so rules engine can avoid checks on revotes

                    //execute rules engine
                    switch (vote)
                    {
                        case 1:
                            outcome = VoatRulesEngine.Instance.EvaluateRuleSet(ruleContext, RuleScope.Vote, RuleScope.VoteComment, RuleScope.UpVote, RuleScope.UpVoteComment);
                            break;

                        case -1:
                            outcome = VoatRulesEngine.Instance.EvaluateRuleSet(ruleContext, RuleScope.Vote, RuleScope.VoteComment, RuleScope.DownVote, RuleScope.DownVoteComment);
                            break;
                    }

                    //return if rules engine denies
                    if (outcome.IsDenied)
                    {
                        return VoteResponse.Create(outcome);
                    }

                    VoteResponse response = new VoteResponse(Status.NotProcessed, 0, "Vote not processed.");
                    switch (existingVote)
                    {
                        case 0: //Never voted or No vote

                            switch (vote)
                            {
                                case 0:
                                    response = VoteResponse.Ignored(0, "A revoke on an unvoted item has opened a worm hole! Run!");
                                    break;

                                case 1:
                                case -1:

                                    if (vote == 1)
                                    {
                                        comment.UpCount++;
                                    }
                                    else
                                    {
                                        comment.DownCount++;
                                    }

                                    var newVotingTracker = new CommentVoteTracker
                                    {
                                        CommentID = commentID,
                                        UserName = userName,
                                        VoteStatus = vote,
                                        VoteValue = GetVoteValue(userName, comment.UserName, ContentType.Comment, comment.ID, vote), //TODO: Need to set this to zero for Anon, MinCCP subs, and Private subs
                                        IPAddress = addressHash,
                                        CreationDate = Repository.CurrentDate
                                    };

                                    _db.CommentVoteTrackers.Add(newVotingTracker);
                                    _db.SaveChanges();

                                    //SendVoteNotification(comment.Name, "upvote");
                                    response = VoteResponse.Successful(vote);
                                    response.Difference = vote;
                                    response.Response = new Score() { DownCount = (int)comment.DownCount, UpCount = (int)comment.UpCount };
                                    break;
                            }
                            break;

                        case 1: //Previous Upvote

                            switch (vote)
                            {
                                case 0: //revoke
                                case 1: //revote which means revoke if we are here

                                    if (existingVoteTracker != null)
                                    {
                                        comment.UpCount--;

                                        _db.CommentVoteTrackers.Remove(existingVoteTracker);
                                        _db.SaveChanges();

                                        response = VoteResponse.Successful(0, REVOKE_MSG);
                                        response.Difference = -1;
                                        response.Response = new Score() { DownCount = (int)comment.DownCount, UpCount = (int)comment.UpCount };
                                    }
                                    break;

                                case -1:

                                    //change upvote to downvote

                                    if (existingVoteTracker != null)
                                    {
                                        comment.UpCount--;
                                        comment.DownCount++;

                                        existingVoteTracker.VoteStatus = vote;
                                        existingVoteTracker.VoteValue = GetVoteValue(userName, comment.UserName, ContentType.Comment, comment.ID, vote);
                                        existingVoteTracker.CreationDate = CurrentDate;
                                        _db.SaveChanges();

                                        response = VoteResponse.Successful(vote);
                                        response.Difference = -2;
                                        response.Response = new Score() { DownCount = (int)comment.DownCount, UpCount = (int)comment.UpCount };
                                    }
                                    break;
                            }
                            break;

                        case -1: //Previous downvote

                            switch (vote)
                            {
                                case 0: //revoke
                                case -1: //revote which means revoke

                                    if (existingVoteTracker != null)
                                    {
                                        comment.DownCount--;
                                        _db.CommentVoteTrackers.Remove(existingVoteTracker);
                                        _db.SaveChanges();
                                        response = VoteResponse.Successful(0, REVOKE_MSG);
                                        response.Difference = 1;
                                        response.Response = new Score() { DownCount = (int)comment.DownCount, UpCount = (int)comment.UpCount };
                                    }
                                    break;

                                case 1:

                                    //change downvote to upvote
                                    if (existingVoteTracker != null)
                                    {
                                        comment.UpCount++;
                                        comment.DownCount--;

                                        existingVoteTracker.VoteStatus = vote;
                                        existingVoteTracker.VoteValue = GetVoteValue(userName, comment.UserName, ContentType.Comment, comment.ID, vote);
                                        existingVoteTracker.CreationDate = CurrentDate;

                                        _db.SaveChanges();
                                        response = VoteResponse.Successful(vote);
                                        response.Difference = 2;
                                        response.Response = new Score() { DownCount = (int)comment.DownCount, UpCount = (int)comment.UpCount };
                                    }

                                    break;
                            }
                            break;
                    }

                    //Set owner user name for notifications
                    response.OwnerUserName = comment.UserName;
                    return response;
                }
            }
            return VoteResponse.Denied();
        }

        [Authorize]
        public VoteResponse VoteSubmission(int submissionID, int vote, string addressHash, bool revokeOnRevote = true)
        {
            DemandAuthentication();

            //make sure we don't have bad int values for vote
            if (Math.Abs(vote) > 1)
            {
                throw new ArgumentOutOfRangeException("vote", "Valid values for vote are only: -1, 0, 1");
            }

            string userName = User.Identity.Name;
            var ruleContext = new VoatRuleContext();
            ruleContext.PropertyBag.AddressHash = addressHash;
            RuleOutcome outcome = null;

            string REVOKE_MSG = "Vote has been revoked";
            Data.Models.Submission submission = null;
            var synclock_submission = _lockStore.GetLockObject(String.Format("submission:{0}", submissionID));
            lock (synclock_submission)
            {
                submission = _db.Submissions.FirstOrDefault(x => x.ID == submissionID);

                if (submission != null)
                {
                    if (submission.IsDeleted)
                    {
                        return VoteResponse.Ignored(0, "Deleted submissions cannot be voted");
                        //return VoteResponse.Ignored(0, "User is prevented from voting on own content");
                    }
                    //if (submission.IsArchived)
                    //{
                    //    return VoteResponse.Ignored(0, "Archived submissions cannot be voted");
                    //    //return VoteResponse.Ignored(0, "User is prevented from voting on own content");
                    //}

                    //ignore votes if user owns it
                    if (String.Equals(submission.UserName, userName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return VoteResponse.Ignored(0, "User is prevented from voting on own content");
                    }

                    //check existing vote
                    int existingVote = 0;
                    var existingVoteTracker = _db.SubmissionVoteTrackers.FirstOrDefault(x => x.SubmissionID == submissionID && x.UserName == userName);
                    if (existingVoteTracker != null)
                    {
                        existingVote = existingVoteTracker.VoteStatus;
                    }

                    // do not execute voting, user has already up/down voted item and is submitting a vote that matches their existing vote
                    if (existingVote == vote && !revokeOnRevote)
                    {
                        return VoteResponse.Ignored(existingVote, "User has already voted this way.");
                    }

                    //set properties for rules engine
                    ruleContext.SubmissionID = submission.ID;
                    ruleContext.PropertyBag.CurrentVoteValue = existingVote; //set existing vote value so rules engine can avoid checks on revotes

                    //execute rules engine
                    switch (vote)
                    {
                        case 1:
                            outcome = VoatRulesEngine.Instance.EvaluateRuleSet(ruleContext, RuleScope.Vote, RuleScope.VoteSubmission, RuleScope.UpVote, RuleScope.UpVoteSubmission);
                            break;

                        case -1:
                            outcome = VoatRulesEngine.Instance.EvaluateRuleSet(ruleContext, RuleScope.Vote, RuleScope.VoteSubmission, RuleScope.DownVote, RuleScope.DownVoteSubmission);
                            break;
                    }

                    //return if rules engine denies
                    if (outcome.IsDenied)
                    {
                        return VoteResponse.Create(outcome);
                    }

                    VoteResponse response = new VoteResponse(Status.NotProcessed, 0, "Vote not processed.");
                    switch (existingVote)
                    {
                        case 0: //Never voted or No vote

                            switch (vote)
                            {
                                case 0: //revoke
                                    response = VoteResponse.Ignored(0, "A revoke on an unvoted item has opened a worm hole! Run!");
                                    break;

                                case 1:
                                case -1:

                                    if (vote == 1)
                                    {
                                        submission.UpCount++;
                                    }
                                    else
                                    {
                                        submission.DownCount++;
                                    }

                                    //calculate new ranks
                                    Ranking.RerankSubmission(submission);

                                    var t = new SubmissionVoteTracker
                                    {
                                        SubmissionID = submissionID,
                                        UserName = userName,
                                        VoteStatus = vote,
                                        VoteValue = GetVoteValue(userName, submission.UserName, ContentType.Submission, submission.ID, vote), //TODO: Need to set this to zero for Anon, MinCCP subs, and Private subs
                                        IPAddress = addressHash,
                                        CreationDate = Repository.CurrentDate
                                    };

                                    _db.SubmissionVoteTrackers.Add(t);
                                    _db.SaveChanges();

                                    response = VoteResponse.Successful(vote);
                                    response.Difference = vote;
                                    response.Response = new Score() { DownCount = (int)submission.DownCount, UpCount = (int)submission.UpCount };
                                    break;
                            }
                            break;

                        case 1: //Previous Upvote

                            switch (vote)
                            {
                                case 0: //revoke
                                case 1: //revote which means revoke if we are here

                                    if (existingVoteTracker != null)
                                    {
                                        submission.UpCount--;

                                        //calculate new ranks
                                        Ranking.RerankSubmission(submission);

                                        _db.SubmissionVoteTrackers.Remove(existingVoteTracker);
                                        _db.SaveChanges();

                                        response = response = VoteResponse.Successful(0, REVOKE_MSG);
                                        response.Difference = -1;
                                        response.Response = new Score() { DownCount = (int)submission.DownCount, UpCount = (int)submission.UpCount };
                                    }
                                    break;

                                case -1:

                                    //change upvote to downvote

                                    if (existingVoteTracker != null)
                                    {
                                        submission.UpCount--;
                                        submission.DownCount++;

                                        //calculate new ranks
                                        Ranking.RerankSubmission(submission);

                                        existingVoteTracker.VoteStatus = vote;
                                        existingVoteTracker.VoteValue = GetVoteValue(userName, submission.UserName, ContentType.Submission, submission.ID, vote);
                                        existingVoteTracker.CreationDate = CurrentDate;

                                        _db.SaveChanges();

                                        response = VoteResponse.Successful(vote);
                                        response.Difference = -2;
                                        response.Response = new Score() { DownCount = (int)submission.DownCount, UpCount = (int)submission.UpCount };
                                    }
                                    break;
                            }
                            break;

                        case -1: //Previous downvote
                            switch (vote)
                            {
                                case 0: //revoke
                                case -1: //revote which means revoke if we are here

                                    // delete existing downvote

                                    if (existingVoteTracker != null)
                                    {
                                        submission.DownCount--;

                                        //calculate new ranks
                                        Ranking.RerankSubmission(submission);

                                        _db.SubmissionVoteTrackers.Remove(existingVoteTracker);
                                        _db.SaveChanges();

                                        response = VoteResponse.Successful(0, REVOKE_MSG);
                                        response.Difference = 1;
                                        response.Response = new Score() { DownCount = (int)submission.DownCount, UpCount = (int)submission.UpCount };
                                    }
                                    break;

                                case 1:

                                    //change downvote to upvote
                                    if (existingVoteTracker != null)
                                    {
                                        submission.UpCount++;
                                        submission.DownCount--;

                                        //calculate new ranks
                                        Ranking.RerankSubmission(submission);

                                        existingVoteTracker.VoteStatus = vote;
                                        existingVoteTracker.VoteValue = GetVoteValue(userName, submission.UserName, ContentType.Submission, submission.ID, vote);
                                        existingVoteTracker.CreationDate = CurrentDate;

                                        _db.SaveChanges();
                                        response = VoteResponse.Successful(vote);
                                        response.Difference = 2;
                                        response.Response = new Score() { DownCount = (int)submission.DownCount, UpCount = (int)submission.UpCount };
                                    }
                                    break;
                            }
                            break;
                    }

                    //Set owner user name for notifications
                    response.OwnerUserName = submission.UserName;
                    return response;
                }
                return VoteResponse.Denied();
            }
        }

        private int GetVoteValue(Subverse subverse, Data.Models.Submission submission, Vote voteStatus)
        {
            if (subverse.IsPrivate || subverse.MinCCPForDownvote > 0 || submission.IsAnonymized)
            {
                return 0;
            }
            return (int)voteStatus;
        }
        private int GetVoteValue(string sourceUser, string targetUser, ContentType contentType, int id, int voteStatus)
        {
            var q = new DapperQuery();
            q.Select = @"sub.IsPrivate, s.IsAnonymized, sub.MinCCPForDownvote FROM Subverse sub WITH (NOLOCK)
                         INNER JOIN Submission s WITH (NOLOCK) ON s.Subverse = sub.Name";

            switch (contentType)
            {
                case ContentType.Comment:
                    q.Select += " INNER JOIN Comment c WITH (NOLOCK) ON c.SubmissionID = s.ID";
                    q.Where = "c.ID = @ID";
                    break;
                case ContentType.Submission:
                    q.Where = "s.ID = @ID";
                    break;
            }

            var record = _db.Database.Connection.QueryFirst(q.ToString(), new { ID = id });

            if (record.IsPrivate || record.IsAnonymized || record.MinCCPForDownvote > 0)
            {
                return 0;
            }
            else
            {
                return voteStatus;
            }
        }
        #endregion Vote

        #region Subverse

        public IEnumerable<SubverseInformation> GetDefaultSubverses()
        {
            var defaults = (from d in _db.DefaultSubverses
                            join x in _db.Subverses on d.Subverse equals x.Name
                            orderby d.Order
                            select new SubverseInformation
                            {
                                Name = x.Name,
                                SubscriberCount = x.SubscriberCount.HasValue ? x.SubscriberCount.Value : 0,
                                CreationDate = x.CreationDate,
                                Description = x.Description,
                                IsAdult = x.IsAdult,
                                Title = x.Title,
                                //Type = x.Type,
                                Sidebar = x.SideBar
                            }).ToList();
            return defaults;
        }

        public IEnumerable<SubverseInformation> GetTopSubscribedSubverses(int count = 200)
        {
            var subs = (from x in _db.Subverses
                        orderby x.SubscriberCount descending
                        select new SubverseInformation
                        {
                            Name = x.Name,
                            SubscriberCount = x.SubscriberCount.HasValue ? x.SubscriberCount.Value : 0,
                            CreationDate = x.CreationDate,
                            Description = x.Description,
                            IsAdult = x.IsAdult,
                            Title = x.Title,
                            //Type = x.Type,
                            Sidebar = x.SideBar
                        }).Take(count).ToList();
            return subs;
        }

        public IEnumerable<SubverseInformation> GetNewestSubverses(int count = 100)
        {
            var subs = (from x in _db.Subverses
                        orderby x.CreationDate descending
                        select new SubverseInformation
                        {
                            Name = x.Name,
                            SubscriberCount = x.SubscriberCount.HasValue ? x.SubscriberCount.Value : 0,
                            CreationDate = x.CreationDate,
                            Description = x.Description,
                            IsAdult = x.IsAdult,
                            Title = x.Title,
                            //Type = x.Type,
                            Sidebar = x.SideBar
                        }
                        ).Take(count).ToList();
            return subs;
        }

        public IEnumerable<SubverseInformation> FindSubverses(string phrase, int count = 50)
        {
            var subs = (from x in _db.Subverses
                        where x.Name.Contains(phrase) || x.Description.Contains(phrase)
                        orderby x.SubscriberCount descending
                        select new SubverseInformation
                        {
                            Name = x.Name,
                            SubscriberCount = x.SubscriberCount.HasValue ? x.SubscriberCount.Value : 0,
                            CreationDate = x.CreationDate,
                            Description = x.Description,
                            IsAdult = x.IsAdult,
                            Title = x.Title,
                            //Type = x.Type,
                            Sidebar = x.SideBar
                        }
                        ).Take(count).ToList();
            return subs;
        }

        public Subverse GetSubverseInfo(string subverse, bool filterDisabled = false)
        {
            using (var db = new voatEntities())
            {
                db.EnableCacheableOutput();
                var query = (from x in db.Subverses
                             where x.Name == subverse
                             select x);
                if (filterDisabled)
                {
                    query = query.Where(x => x.IsAdminDisabled != true);
                }
                var submission = query.FirstOrDefault();
                return submission;
            }
        }

        public string GetSubverseStylesheet(string subverse)
        {
            var sheet = (from x in _db.Subverses
                         where x.Name.Equals(subverse, StringComparison.OrdinalIgnoreCase)
                         select x.Stylesheet).FirstOrDefault();
            return String.IsNullOrEmpty(sheet) ? "" : sheet;
        }

        public IEnumerable<Data.Models.SubverseModerator> GetSubverseModerators(string subverse)
        {
            var data = (from x in _db.SubverseModerators
                        where x.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase)
                        orderby x.CreationDate ascending
                        select x).ToList();

            return data.AsEnumerable();
        }

        public IEnumerable<Data.Models.SubverseModerator> GetSubversesUserModerates(string userName)
        {
            var data = (from x in _db.SubverseModerators
                        where x.UserName == userName
                        select x).ToList();

            return data.AsEnumerable();
        }

        public async Task<CommandResponse> CreateSubverse(string name, string title, string description, string sidebar = null)
        {
            DemandAuthentication();
            
            //Evaulate Rules
            VoatRuleContext context = new VoatRuleContext();
            context.PropertyBag.SubverseName = name;
            var outcome = VoatRulesEngine.Instance.EvaluateRuleSet(context, RuleScope.CreateSubverse);
            if (!outcome.IsAllowed)
            {
                return MapRuleOutCome<object>(outcome, null);
            }

            try
            {
                // setup default values and create the subverse
                var subverse = new Subverse
                {
                    Name = name,
                    Title = title,
                    Description = description,
                    SideBar = sidebar,
                    CreationDate = Repository.CurrentDate,
                    //Type = "link",
                    IsThumbnailEnabled = true,
                    IsAdult = false,
                    IsPrivate = false,
                    MinCCPForDownvote = 0,
                    IsAdminDisabled = false,
                    CreatedBy = User.Identity.Name,
                    SubscriberCount = 0
                };

                _db.Subverses.Add(subverse);
                await _db.SaveChangesAsync().ConfigureAwait(false);

                await SubscribeUser(new DomainReference(DomainType.Subverse, subverse.Name), SubscriptionAction.Subscribe).ConfigureAwait(false);


                // register user as the owner of the newly created subverse
                var tmpSubverseAdmin = new Models.SubverseModerator
                {
                    Subverse = name,
                    UserName = User.Identity.Name,
                    Power = 1
                };
                _db.SubverseModerators.Add(tmpSubverseAdmin);
                await _db.SaveChangesAsync().ConfigureAwait(false);


                // go to newly created Subverse
                return CommandResponse.Successful();
            }
            catch (Exception ex)
            {
                return CommandResponse.Error<CommandResponse>(ex);
            }
        }
        public async Task<Domain.Models.Submission> GetSticky(string subverse)
        {
            var x = await _db.StickiedSubmissions.FirstOrDefaultAsync(s => s.Subverse == subverse).ConfigureAwait(false);
            if (x != null)
            {
                var submission = GetSubmission(x.SubmissionID);
                if (!submission.IsDeleted)
                {
                    return submission.Map();
                }
            }
            return null;
        }
        #endregion Subverse

        #region Submissions

        public int GetCommentCount(int submissionID)
        {
            using (voatEntities db = new voatEntities())
            {
                var cmd = db.Database.Connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Comment WITH (NOLOCK) WHERE SubmissionID = @SubmissionID AND IsDeleted != 1";
                var param = cmd.CreateParameter();
                param.ParameterName = "SubmissionID";
                param.DbType = System.Data.DbType.Int32;
                param.Value = submissionID;
                cmd.Parameters.Add(param);

                if (cmd.Connection.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }
                return (int)cmd.ExecuteScalar();
            }
        }

        public IEnumerable<Data.Models.Submission> GetTopViewedSubmissions()
        {
            var startDate = CurrentDate.Add(new TimeSpan(0, -24, 0, 0, 0));
            var data = (from submission in _db.Submissions
                        join subverse in _db.Subverses on submission.Subverse equals subverse.Name
                        where submission.ArchiveDate == null && !submission.IsDeleted && subverse.IsPrivate != true && subverse.IsAdminPrivate != true && subverse.IsAdult == false && submission.CreationDate >= startDate && submission.CreationDate <= CurrentDate
                        where !(from bu in _db.BannedUsers select bu.UserName).Contains(submission.UserName)
                        where !subverse.IsAdminDisabled.Value

                        //where !(from ubs in _db.UserBlockedSubverses where ubs.Subverse.Equals(subverse.Name) select ubs.UserName).Contains(User.Identity.Name)
                        orderby submission.Views descending
                        select submission).Take(5).ToList();
            return data.AsEnumerable();
        }

        public string SubverseForSubmission(int submissionID)
        {
            var subname = (from x in _db.Submissions
                           where x.ID == submissionID
                           select x.Subverse).FirstOrDefault();
            return subname;
        }

        public Models.Submission GetSubmission(int submissionID)
        {
            var record = Selectors.SecureSubmission(GetSubmissionUnprotected(submissionID));
            return record;
        }

        public string GetSubmissionOwnerName(int submissionID)
        {
            var result = (from x in _db.Submissions
                          where x.ID == submissionID
                          select x.UserName).FirstOrDefault();
            return result;
        }

        public Models.Submission FindSubverseLinkSubmission(string subverse, string url, TimeSpan cutOffTimeSpan)
        {
            var cutOffDate = CurrentDate.Subtract(cutOffTimeSpan);
            return _db.Submissions.AsNoTracking().FirstOrDefault(s =>
                s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)
                && s.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase)
                && s.CreationDate > cutOffDate
                && !s.IsDeleted);
        }

        public int FindUserLinkSubmissionCount(string userName, string url, TimeSpan cutOffTimeSpan)
        {
            var cutOffDate = CurrentDate.Subtract(cutOffTimeSpan);
            return _db.Submissions.Count(s =>
                s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)
                && s.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
                && s.CreationDate > cutOffDate);
        }

        private Models.Submission GetSubmissionUnprotected(int submissionID)
        {
            var query = (from x in _db.Submissions.AsNoTracking()
                         where x.ID == submissionID
                         select x);

            var record = query.FirstOrDefault();
            return record;
        }

        public async Task<IEnumerable<Models.Submission>> GetUserSubmissions(string subverse, string userName, SearchOptions options)
        {
            //This is a near copy of GetSubmissions<T>
            if (String.IsNullOrEmpty(userName))
            {
                throw new VoatValidationException("A username must be provided.");
            }
            if (!String.IsNullOrEmpty(subverse) && !SubverseExists(subverse))
            {
                throw new VoatValidationException("Subverse '{0}' doesn't exist.", subverse);
            }
            if (!UserHelper.UserExists(userName))
            {
                throw new VoatValidationException("User does not exist.");
            }

            IQueryable<Models.Submission> query;

            subverse = ToCorrectSubverseCasing(subverse);

            query = (from x in _db.Submissions
                     where (
                        x.UserName == userName 
                        && !x.IsAnonymized 
                        && !x.IsDeleted
                        )
                     && (x.Subverse == subverse || subverse == null)
                     select x);

            query = ApplySubmissionSearch(options, query);

            //execute query
            var results = (await query.ToListAsync().ConfigureAwait(false)).Select(Selectors.SecureSubmission);

            return results;
        }
        public async Task<IEnumerable<Data.Models.Submission>> GetSubmissionsByDomain(string domain, SearchOptions options)
        {
            var query = new DapperQuery();
            query.SelectColumns = "s.*";
            query.Select = @"SELECT DISTINCT {0} FROM Submission s WITH (NOLOCK) INNER JOIN Subverse sub WITH (NOLOCK) ON s.Subverse = sub.Name";
            query.Where = $"s.Type = {((int)SubmissionType.Link).ToString()} AND s.Url LIKE CONCAT('%', @Domain, '%')";
            ApplySubmissionSort(query, options);

            query.SkipCount = options.Index;
            query.TakeCount = options.Count;

            //Filter out all disabled subs
            query.Append(x => x.Where, "sub.IsAdminDisabled = 0");
            query.Append(x => x.Where, "s.IsDeleted = 0");

            query.Parameters = (new { Domain = domain }).ToDynamicParameters();

            //execute query
            var data = await _db.Database.Connection.QueryAsync<Data.Models.Submission>(query.ToString(), query.Parameters);
            var results = data.Select(Selectors.SecureSubmission).ToList();
            return results;
        }
        public async Task<IEnumerable<Data.Models.Submission>> GetSubmissions(params int[] submissionID)
        {
            var query = new DapperQuery();
            query.SelectColumns = "s.*";
            query.Select = @"SELECT DISTINCT {0} FROM Submission s WITH (NOLOCK)";
            query.Where = "ID IN @IDs";
            query.Parameters = (new { IDs = submissionID }).ToDynamicParameters();

            //execute query
            var data = await _db.Database.Connection.QueryAsync<Data.Models.Submission>(query.ToString(), query.Parameters);
            var results = data.Select(Selectors.SecureSubmission).ToList();
            return results;
        }

        public async Task<IEnumerable<Data.Models.Submission>> GetSubmissionsDapper(DomainReference domainReference, SearchOptions options)
        {
            //backwards compat with function body
            var type = domainReference.Type;
            var name = domainReference.Name;
            var ownerName = domainReference.OwnerName;

            if (!(type == DomainType.Subverse || type == DomainType.Set))
            {
                throw new NotImplementedException($"DomainType {type.ToString()} not implemented using this pipeline");
            }

            if (String.IsNullOrEmpty(name))
            {
                throw new VoatValidationException("An object name must be provided.");
            }

            if (options == null)
            {
                options = new SearchOptions();
            }

            var query = new DapperQuery();
            query.SelectColumns = "s.*";
            query.Select = @"SELECT DISTINCT {0} FROM Submission s WITH (NOLOCK) INNER JOIN Subverse sub WITH (NOLOCK) ON s.Subverse = sub.Name";

            //Parameter Declarations
            DateTime? startDate = options.StartDate;
            DateTime? endDate = options.EndDate;
            //string subverse = subverse;
            bool nsfw = false;
            string userName = null;
            
            UserData userData = null;
            if (User.Identity.IsAuthenticated)
            {
                userData = new UserData(User.Identity.Name);
                userName = userData.UserName;
            }


            var joinSet = new Action<DapperQuery, string, string, SetType?, bool>((q, setName, setOwnerName, setType, include) => {

                 var set = GetSet(setName, setOwnerName, setType);
                if (set != null)
                {
                    var joinAlias = $"set{setName}";
                    var op = include ? "=" : "!=";
                    query.Append(x => x.Select, $"INNER JOIN [dbo].[SubverseSetList] {joinAlias} WITH (NOLOCK) ON sub.ID {op} {joinAlias}.SubverseID");
                    query.Append(x => x.Where, $"{joinAlias}.SubverseSetID = @{joinAlias}ID");
                    query.Parameters.Add($"{joinAlias}ID", set.ID);
                }
            });


            switch (type) {
                case DomainType.Subverse:
           
                    bool filterBlockedSubverses = false;

                    switch (name.ToLower())
                    {
                        //Match Aggregate Subs
                        case AGGREGATE_SUBVERSE.FRONT:
                            joinSet(query, SetType.Front.ToString(), userName, SetType.Front, true);
                            //query.Append(x => x.Select, "INNER JOIN SubverseSubscription ss WITH (NOLOCK) ON s.Subverse = ss.Subverse");
                            query.Append(x => x.Where, "s.ArchiveDate IS NULL AND s.IsDeleted = 0");

                            //query = (from x in _db.Submissions
                            //         join subscribed in _db.SubverseSubscriptions on x.Subverse equals subscribed.Subverse
                            //         where subscribed.UserName == User.Identity.Name
                            //         select x);
                   
                            break;
                        case AGGREGATE_SUBVERSE.DEFAULT:
                            //if no user or user has no subscriptions or logged in user requests default page
                            joinSet(query, "Default", null, null, true);
                            //query.Append(x => x.Select, "INNER JOIN DefaultSubverse ss WITH (NOLOCK) ON s.Subverse = ss.Subverse");

                            if (Settings.IsVoatBranded)
                            {
                                //This is a modification Voat uses in the default page
                                query.Append(x => x.Where, "(s.UpCount - s.DownCount >= 20) AND ABS(DATEDIFF(HH, s.CreationDate, GETUTCDATE())) <= 24");
                            }

                            //sort default by relative rank
                            options.Sort = Domain.Models.SortAlgorithm.RelativeRank;

                            //query = (from x in _db.Submissions
                            //         join defaults in _db.DefaultSubverses on x.Subverse equals defaults.Subverse
                            //         select x);
                            break;
                        case AGGREGATE_SUBVERSE.ANY:
                            //allowing subverse marked private to not be filtered
                            //Should subs marked as private be excluded from an ANY query? I don't know.
                            //query.Where = "sub.IsAdminPrivate = 0 AND sub.IsPrivate = 0";
                            query.Where = "sub.IsAdminPrivate = 0";
                            //query = (from x in _db.Submissions
                            //         where
                            //         !x.Subverse1.IsAdminPrivate
                            //         && !x.Subverse1.IsPrivate
                            //         && !(x.Subverse1.IsAdminDisabled.HasValue && x.Subverse1.IsAdminDisabled.Value)
                            //         select x);
                            break;

                        case AGGREGATE_SUBVERSE.ALL:
                        case "all":
                            filterBlockedSubverses = true;
                            ////Controller logic:
                            //IQueryable<Submission> submissionsFromAllSubversesByDate = 
                            //(from message in _db.Submissions
                            //join subverse in _db.Subverses on message.Subverse equals subverse.Name
                            //where !message.IsArchived && !message.IsDeleted && subverse.IsPrivate != true && subverse.IsAdminPrivate != true && subverse.MinCCPForDownvote == 0
                            //where !(from bu in _db.BannedUsers select bu.UserName).Contains(message.UserName)
                            //where !subverse.IsAdminDisabled.Value
                            //where !(from ubs in _db.UserBlockedSubverses where ubs.Subverse.Equals(subverse.Name) select ubs.UserName).Contains(userName)
                            //select message).OrderByDescending(s => s.CreationDate).AsNoTracking();

                            nsfw = (User.Identity.IsAuthenticated ? userData.Preferences.EnableAdultContent : false);

                            //v/all has certain conditions
                            //1. Only subs that have a MinCCP of zero
                            //2. Don't show private subs
                            //3. Don't show NSFW subs if nsfw isn't enabled in profile, if they are logged in
                            //4. Don't show blocked subs if logged in // not implemented
                            query.Where = "sub.MinCCPForDownvote = 0 AND sub.IsAdminPrivate = 0 AND sub.IsPrivate = 0";
                            if (!nsfw)
                            {
                                query.Where += " AND sub.IsAdult = 0 AND s.IsAdult = 0";
                            }

                            //query = (from x in _db.Submissions
                            //         where x.Subverse1.MinCCPForDownvote == 0
                            //                && (!x.Subverse1.IsAdminPrivate && !x.Subverse1.IsPrivate && !(x.Subverse1.IsAdminDisabled.HasValue && x.Subverse1.IsAdminDisabled.Value))
                            //                && (x.Subverse1.IsAdult && nsfw || !x.Subverse1.IsAdult)
                            //         select x);
                            break;

                        //for regular subverse queries
                        default:

                            if (!SubverseExists(name))
                            {
                                throw new VoatNotFoundException("Subverse '{0}' not found.", name);
                            }

                            ////Controller Logic:
                            //IQueryable<Submission> submissionsFromASubverseByDate = 
                            //    (from message in _db.Submissions
                            //    join subverse in _db.Subverses on message.Subverse equals subverse.Name
                            //    where !message.IsDeleted && message.Subverse == subverseName
                            //    where !(from bu in _db.BannedUsers select bu.UserName).Contains(message.UserName)
                            //    where !(from bu in _db.SubverseBans where bu.Subverse == subverse.Name select bu.UserName).Contains(message.UserName)
                            //    select message).OrderByDescending(s => s.CreationDate).AsNoTracking();

                            name = ToCorrectSubverseCasing(name);
                            query.Where = "s.Subverse = @Name";

                            ////Filter out stickies in subs
                            //query.Append(x => x.Where, "s.ID NOT IN (SELECT sticky.SubmissionID FROM StickiedSubmission sticky WITH (NOLOCK) WHERE sticky.SubmissionID = s.ID AND sticky.Subverse = s.Subverse)");
                    
                            //query = (from x in _db.Submissions
                            //         where (x.Subverse == subverse || subverse == null)
                            //         select x);
                            break;
                    }
                    //Filter out stickies
                    switch (name.ToLower())
                    {
                        //Match Aggregate Subs
                        case AGGREGATE_SUBVERSE.FRONT:
                        case AGGREGATE_SUBVERSE.DEFAULT:
                        case AGGREGATE_SUBVERSE.ANY:
                        case AGGREGATE_SUBVERSE.ALL:
                        case "all":

                            query.Append(x => x.Where, "s.ID NOT IN (SELECT sticky.SubmissionID FROM StickiedSubmission sticky WITH (NOLOCK) WHERE sticky.SubmissionID = s.ID AND sticky.Subverse = 'announcements')");

                            break;
                        //for regular subverse queries
                        default:

                            //Filter out stickies in subs
                            query.Append(x => x.Where, "s.ID NOT IN (SELECT sticky.SubmissionID FROM StickiedSubmission sticky WITH (NOLOCK) WHERE sticky.SubmissionID = s.ID AND sticky.Subverse = s.Subverse)");
                            break;
                    }


                    

                    if (User.Identity.IsAuthenticated)
                    {
                        if (filterBlockedSubverses)
                        {
                            var set = GetSet(SetType.Blocked.ToString(), userName, SetType.Blocked);
                            if (set != null)
                            {
                                query.Append(x => x.Where, "sub.ID NOT IN (SELECT SubverseID FROM SubverseSetList WHERE SubverseSetID = @BlockedSetID)");
                                query.Parameters.Add("BlockedSetID", set.ID);
                            }
                        }
                    }
                    break;

                case DomainType.Set:
                    joinSet(query, name, ownerName, null, true);
                    break;
            }

            query.Append(x => x.Where, "s.IsDeleted = 0");

            //TODO: Re-implement this logic
            //HACK: Warning, Super hacktastic
            if (!String.IsNullOrEmpty(options.Phrase))
            {
                query.Append(x => x.Where, "(s.Title LIKE CONCAT('%', @Phrase, '%') OR s.Content LIKE CONCAT('%', @Phrase, '%') OR s.Url LIKE CONCAT('%', @Phrase, '%'))");
                ////WARNING: This is a quickie that views spaces as AND conditions in a search.
                //List<string> keywords = null;
                //if (options.Phrase.Contains(" "))
                //{
                //    keywords = new List<string>(options.Phrase.Split(' '));
                //}
                //else
                //{
                //    keywords = new List<string>(new string[] { options.Phrase });
                //}

                //keywords.ForEach(x =>
                //{
                //    query = query.Where(m => m.Title.Contains(x) || m.Content.Contains(x) || m.Url.Contains(x));
                //});
            }

            ApplySubmissionSort(query, options);

            query.SkipCount = options.Index;
            query.TakeCount = options.Count;

            //Filter out all disabled subs
            query.Append(x => x.Where, "sub.IsAdminDisabled = 0");

            query.Parameters.Add("StartDate", startDate);
            query.Parameters.Add("EndDate", endDate);
            query.Parameters.Add("Name", name);
            query.Parameters.Add("UserName", userName);
            query.Parameters.Add("Phrase", options.Phrase);

            //execute query
            var queryString = query.ToString();

           var data = await _db.Database.Connection.QueryAsync<Data.Models.Submission>(queryString, query.Parameters);
            var results = data.Select(Selectors.SecureSubmission).ToList();
            return results;
        }
        private void ApplySubmissionSort(DapperQuery query, SearchOptions options)
        {
            #region Ordering


            if (options.StartDate.HasValue)
            {
                query.Where += " AND s.CreationDate >= @StartDate";
                //query = query.Where(x => x.CreationDate >= options.StartDate.Value);
            }
            if (options.EndDate.HasValue)
            {
                query.Where += " AND s.CreationDate <= @EndDate";
                //query = query.Where(x => x.CreationDate <= options.EndDate.Value);
            }

            //Search Options
            switch (options.Sort)
            {
                case SortAlgorithm.RelativeRank:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = "s.RelativeRank ASC";
                        //query = query.OrderBy(x => x.RelativeRank);
                    }
                    else
                    {
                        query.OrderBy = "s.RelativeRank DESC";
                        //query = query.OrderByDescending(x => x.RelativeRank);
                    }
                    break;

                case SortAlgorithm.Rank:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = "s.Rank ASC";
                        //query = query.OrderBy(x => x.Rank);
                    }
                    else
                    {
                        query.OrderBy = "s.Rank DESC";
                        //query = query.OrderByDescending(x => x.Rank);
                    }
                    break;

                case SortAlgorithm.Top:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = "s.UpCount ASC";
                        //query = query.OrderBy(x => x.UpCount);
                    }
                    else
                    {
                        query.OrderBy = "s.UpCount DESC";
                        //query = query.OrderByDescending(x => x.UpCount);
                    }
                    break;

                case SortAlgorithm.Viewed:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = "s.Views ASC";
                        //query = query.OrderBy(x => x.Views);
                    }
                    else
                    {
                        query.OrderBy = "s.Views DESC";
                        //query = query.OrderByDescending(x => x.Views);
                    }
                    break;
                //Need to verify performance of these before using
                //case SortAlgorithm.Discussed:
                //    if (options.SortDirection == SortDirection.Reverse)
                //    {
                //        query = query.OrderBy(x => x.Comments.Count);
                //    }
                //    else
                //    {
                //        query = query.OrderByDescending(x => x.Comments.Count);
                //    }
                //    break;

                //case SortAlgorithm.Active:
                //    if (options.SortDirection == SortDirection.Reverse)
                //    {
                //        query = query.OrderBy(x => x.Comments.OrderBy(c => c.CreationDate).FirstOrDefault().CreationDate);
                //    }
                //    else
                //    {
                //        query = query.OrderByDescending(x => x.Comments.OrderBy(c => c.CreationDate).FirstOrDefault().CreationDate);
                //    }
                //    break;

                case SortAlgorithm.Bottom:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = "s.DownCount ASC";
                        //query = query.OrderBy(x => x.DownCount);
                    }
                    else
                    {
                        query.OrderBy = "s.DownCount DESC";
                        //query = query.OrderByDescending(x => x.DownCount);
                    }
                    break;
                //case SortAlgorithm.Active:
                //string activeSort = "s.LastCommentDate";
                ////query.SelectColumns = query.AppendClause(query.SelectColumns, "LastCommentDate = (SELECT TOP 1 ISNULL(c.CreationDate, s.CreationDate) FROM Comment c WITH (NOLOCK) WHERE c.SubmissionID = s.ID ORDER BY c.CreationDate DESC)", ", ");
                //if (options.SortDirection == SortDirection.Reverse)
                //{
                //    query.OrderBy = $"{activeSort} ASC";
                //}
                //else
                //{
                //    query.OrderBy = $"{activeSort} DESC";
                //}
                //break;

                case SortAlgorithm.Intensity:
                    string sort = "(s.UpCount + s.DownCount)";
                    query.SelectColumns = query.AppendClause(query.SelectColumns, sort, ", ");
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = $"{sort} ASC";
                        //query = query.OrderBy(x => x.UpCount + x.DownCount);
                    }
                    else
                    {
                        query.OrderBy = $"{sort} DESC";
                        //query = query.OrderByDescending(x => x.UpCount + x.DownCount);
                    }
                    break;

                //making this default for easy debugging
                case SortAlgorithm.New:
                default:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query.OrderBy = "s.CreationDate ASC";
                        //query = query.OrderBy(x => x.CreationDate);
                    }
                    else
                    {
                        query.OrderBy = "s.CreationDate DESC";
                        //query = query.OrderByDescending(x => x.CreationDate);
                    }
                    break;
            }

            //query = query.Skip(options.Index).Take(options.Count);
            //return query;

            #endregion
        }
        //[Obsolete("Moving to Dapper, see yall later", true)]
        //public async Task<IEnumerable<Models.Submission>> GetSubmissions(string subverse, SearchOptions options)
        //{
        //    if (String.IsNullOrEmpty(subverse))
        //    {
        //        throw new VoatValidationException("A subverse must be provided.");
        //    }

        //    if (options == null)
        //    {
        //        options = new SearchOptions();
        //    }

        //    IQueryable<Models.Submission> query;

        //    UserData userData = null;
        //    if (User.Identity.IsAuthenticated)
        //    {
        //        userData = new UserData(User.Identity.Name);
        //    }

        //    switch (subverse.ToLower())
        //    {
        //        //for *special* subverses, this is UNDONE
        //        case AGGREGATE_SUBVERSE.FRONT:
        //            if (User.Identity.IsAuthenticated && userData.HasSubscriptions())
        //            {
        //                query = (from x in _db.Submissions
        //                         join subscribed in _db.SubverseSubscriptions on x.Subverse equals subscribed.Subverse
        //                         where subscribed.UserName == User.Identity.Name
        //                         select x);
        //            }
        //            else
        //            {
        //                //if no user, default to default
        //                query = (from x in _db.Submissions
        //                         join defaults in _db.DefaultSubverses on x.Subverse equals defaults.Subverse
        //                         select x);
        //            }
        //            break;

        //        case AGGREGATE_SUBVERSE.DEFAULT:

        //            query = (from x in _db.Submissions
        //                     join defaults in _db.DefaultSubverses on x.Subverse equals defaults.Subverse
        //                     select x);
        //            break;

        //        case AGGREGATE_SUBVERSE.ANY:

        //            query = (from x in _db.Submissions
        //                     where
        //                     !x.Subverse1.IsAdminPrivate
        //                     && !x.Subverse1.IsPrivate
        //                     && !(x.Subverse1.IsAdminDisabled.HasValue && x.Subverse1.IsAdminDisabled.Value)
        //                     select x);
        //            break;

        //        case AGGREGATE_SUBVERSE.ALL:
        //        case "all":

        //            var nsfw = (User.Identity.IsAuthenticated ? userData.Preferences.EnableAdultContent : false);

        //            //v/all has certain conditions
        //            //1. Only subs that have a MinCCP of zero
        //            //2. Don't show private subs
        //            //3. Don't show NSFW subs if nsfw isn't enabled in profile, if they are logged in
        //            //4. Don't show blocked subs if logged in // not implemented

        //            query = (from x in _db.Submissions
        //                     where x.Subverse1.MinCCPForDownvote == 0
        //                            && (!x.Subverse1.IsAdminPrivate && !x.Subverse1.IsPrivate && !(x.Subverse1.IsAdminDisabled.HasValue && x.Subverse1.IsAdminDisabled.Value))
        //                            && (x.Subverse1.IsAdult && nsfw || !x.Subverse1.IsAdult)
        //                     select x);

        //            break;

        //        //for regular subverse queries
        //        default:

        //            if (!SubverseExists(subverse))
        //            {
        //                throw new VoatNotFoundException("Subverse '{0}' not found.", subverse);
        //            }

        //            subverse = ToCorrectSubverseCasing(subverse);

        //            query = (from x in _db.Submissions
        //                     where (x.Subverse == subverse || subverse == null)
        //                     select x);
        //            break;
        //    }

        //    query = query.Where(x => !x.IsDeleted);

        //    if (User.Identity.IsAuthenticated)
        //    {
        //        //filter blocked subs
        //        query = query.Where(s => !_db.UserBlockedSubverses.Where(b =>
        //            b.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase)
        //            && b.Subverse.Equals(s.Subverse, StringComparison.OrdinalIgnoreCase)).Any());

        //        //filter blocked users (Currently commented out do to a collation issue)
        //        query = query.Where(s => !_db.UserBlockedUsers.Where(b =>
        //            b.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase)
        //            && s.UserName.Equals(b.BlockUser, StringComparison.OrdinalIgnoreCase)
        //            ).Any());

        //        //filter global banned users
        //        query = query.Where(s => !_db.BannedUsers.Where(b => b.UserName.Equals(s.UserName, StringComparison.OrdinalIgnoreCase)).Any());
        //    }

        //    query = ApplySubmissionSearch(options, query);

        //    //execute query
        //    var data = await query.ToListAsync().ConfigureAwait(false);

        //    var results = data.Select(Selectors.SecureSubmission).ToList();

        //    return results;
        //}

        [Authorize]
        public async Task<CommandResponse<Models.Submission>> PostSubmission(UserSubmission userSubmission)
        {
            DemandAuthentication();

            //Load Subverse Object
            //var cmdSubverse = new QuerySubverse(userSubmission.Subverse);
            var subverseObject = _db.Subverses.Where(x => x.Name.Equals(userSubmission.Subverse, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

            //Evaluate Rules
            var context = new VoatRuleContext();
            context.Subverse = subverseObject;
            context.PropertyBag.UserSubmission = userSubmission;
            var outcome = VoatRulesEngine.Instance.EvaluateRuleSet(context, RuleScope.Post, RuleScope.PostSubmission);

            //if rules engine denies bail.
            if (!outcome.IsAllowed)
            {
                return MapRuleOutCome<Models.Submission>(outcome, null);
            }

            //Save submission
            Models.Submission newSubmission = new Models.Submission();
            newSubmission.UpCount = 1; //https://voat.co/v/PreviewAPI/comments/877596
            newSubmission.UserName = User.Identity.Name;
            newSubmission.CreationDate = CurrentDate;
            newSubmission.Subverse = subverseObject.Name;

            //TODO: Should be in rule object
            //If IsAnonymized is NULL, this means subverse allows users to submit either anon or non-anon content
            if (subverseObject.IsAnonymized.HasValue)
            {
                //Trap for a anon submittal in a non-anon subverse
                if (!subverseObject.IsAnonymized.Value && userSubmission.IsAnonymized)
                {
                    return MapRuleOutCome<Models.Submission>(new RuleOutcome(RuleResult.Denied, "Anon Submission Rule", "9.1", "Subverse does not allow anon content"), null);
                }
                newSubmission.IsAnonymized = subverseObject.IsAnonymized.Value;
            }
            else
            {
                newSubmission.IsAnonymized = userSubmission.IsAnonymized;
            }

            //TODO: Determine if subverse is marked as adult or has NSFW in title
            if (subverseObject.IsAdult || (!userSubmission.IsAdult && Regex.IsMatch(userSubmission.Title, CONSTANTS.NSFW_FLAG, RegexOptions.IgnoreCase)))
            {
                userSubmission.IsAdult = true;
            }
            newSubmission.IsAdult = userSubmission.IsAdult;

            //1: Text, 2: Link
            newSubmission.Type = (int)userSubmission.Type;

            if (userSubmission.Type == SubmissionType.Text)
            {
                if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPreSave))
                {
                    userSubmission.Content = ContentProcessor.Instance.Process(userSubmission.Content, ProcessingStage.InboundPreSave, newSubmission);
                }

                newSubmission.Title = userSubmission.Title;
                newSubmission.Content = userSubmission.Content;
                newSubmission.FormattedContent = Formatting.FormatMessage(userSubmission.Content, true);
            }
            else
            {
                newSubmission.Title = userSubmission.Title;
                newSubmission.Url = userSubmission.Url;

                if (subverseObject.IsThumbnailEnabled)
                {
                    // try to generate and assign a thumbnail to submission model
                    newSubmission.Thumbnail = await ThumbGenerator.GenerateThumbFromWebpageUrl(userSubmission.Url).ConfigureAwait(false);
                }
            }

            //Add User Vote to Submission
            newSubmission.SubmissionVoteTrackers.Add(new SubmissionVoteTracker() {
                UserName = newSubmission.UserName,
                VoteStatus = (int)Vote.Up,
                VoteValue = GetVoteValue(subverseObject, newSubmission, Vote.Up),
                IPAddress = null,
                CreationDate = Repository.CurrentDate,
            });
            _db.Submissions.Add(newSubmission);

            await _db.SaveChangesAsync().ConfigureAwait(false);

            //This sends notifications by parsing content
            if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPostSave))
            {
                ContentProcessor.Instance.Process(String.Concat(newSubmission.Title, " ", newSubmission.Content), ProcessingStage.InboundPostSave, newSubmission);
            }

            return CommandResponse.Successful(Selectors.SecureSubmission(newSubmission));
        }

        [Authorize]
        public async Task<CommandResponse<Models.Submission>> EditSubmission(int submissionID, UserSubmission userSubmission)
        {
            DemandAuthentication();

            if (userSubmission == null || (!userSubmission.HasState && String.IsNullOrEmpty(userSubmission.Content)))
            {
                throw new VoatValidationException("The submission must not be null or have invalid state");
            }

            //if (String.IsNullOrEmpty(submission.Url) && String.IsNullOrEmpty(submission.Content)) {
            //    throw new VoatValidationException("Either a Url or Content must be provided.");
            //}

            var submission = _db.Submissions.Where(x => x.ID == submissionID).FirstOrDefault();

            if (submission == null)
            {
                throw new VoatNotFoundException(String.Format("Can't find submission with ID {0}", submissionID));
            }

            if (submission.IsDeleted)
            {
                throw new VoatValidationException("Deleted submissions cannot be edited");
            }

            if (submission.UserName != User.Identity.Name)
            {
                throw new VoatSecurityException(String.Format("Submission can not be edited by account"));
            }

            //Evaluate Rules
            var context = new VoatRuleContext();
            context.Subverse = DataCache.Subverse.Retrieve(submission.Subverse);
            context.PropertyBag.UserSubmission = userSubmission;
            var outcome = VoatRulesEngine.Instance.EvaluateRuleSet(context, RuleScope.EditSubmission);

            //if rules engine denies bail.
            if (!outcome.IsAllowed)
            {
                return MapRuleOutCome<Models.Submission>(outcome, null);
            }


            //only allow edits for self posts
            if (submission.Type == 1)
            {
                submission.Content = userSubmission.Content ?? submission.Content;
                submission.FormattedContent = Formatting.FormatMessage(submission.Content, true);
            }

            //allow edit of title if in 10 minute window
            if (CurrentDate.Subtract(submission.CreationDate).TotalMinutes <= 10.0f)
            {
                if (!String.IsNullOrEmpty(userSubmission.Title) && Formatting.ContainsUnicode(userSubmission.Title))
                {
                    throw new VoatValidationException("Submission title can not contain Unicode characters");
                }

                submission.Title = (String.IsNullOrEmpty(userSubmission.Title) ? submission.Title : userSubmission.Title);
            }

            submission.LastEditDate = CurrentDate;

            await _db.SaveChangesAsync().ConfigureAwait(false);

            return CommandResponse.FromStatus(Selectors.SecureSubmission(submission), Status.Success, "");
        }

        [Authorize]

        //LOGIC COPIED FROM SubmissionController.DeleteSubmission(int)
        public Models.Submission DeleteSubmission(int submissionID, string reason = null)
        {
            DemandAuthentication();

            var submission = _db.Submissions.Find(submissionID);

            if (submission != null && !submission.IsDeleted)
            {
                // delete submission if delete request is issued by submission author
                if (submission.UserName == User.Identity.Name)
                {
                    submission.IsDeleted = true;

                    if (submission.Type == (int)SubmissionType.Text)
                    {
                        submission.Content = UserDeletedContentMessage();
                        submission.FormattedContent = Formatting.FormatMessage(submission.Content);
                    }
                    else
                    {
                        submission.Url = "http://voat.co";
                    }

                    // remove sticky if submission was stickied
                    var existingSticky = _db.StickiedSubmissions.FirstOrDefault(s => s.SubmissionID == submissionID);
                    if (existingSticky != null)
                    {
                        _db.StickiedSubmissions.Remove(existingSticky);
                    }

                    _db.SaveChanges();
                }

                // delete submission if delete request is issued by subverse moderator
                else if (ModeratorPermission.HasPermission(User.Identity.Name, submission.Subverse, ModeratorAction.DeletePosts))
                {
                    if (String.IsNullOrEmpty(reason))
                    {
                        var ex = new VoatValidationException("A reason for deletion is required");
                        ex.Data["SubmissionID"] = submissionID;
                        throw ex;
                    }

                    // mark submission as deleted
                    submission.IsDeleted = true;

                    // move the submission to removal log
                    var removalLog = new SubmissionRemovalLog
                    {
                        SubmissionID = submission.ID,
                        Moderator = User.Identity.Name,
                        Reason = reason,
                        CreationDate = Repository.CurrentDate
                    };

                    _db.SubmissionRemovalLogs.Add(removalLog);
                    var contentPath = VoatPathHelper.CommentsPagePath(submission.Subverse, submission.ID);

                    // notify submission author that his submission has been deleted by a moderator
                    var message = new Domain.Models.SendMessage()
                    {
                        Sender = $"v/{submission.Subverse}",
                        Recipient = submission.UserName,
                        Subject = $"Submission {contentPath} deleted",
                        Message = "Your submission [" + contentPath + "](" + contentPath + ") has been deleted by: " +
                                    "@" + User.Identity.Name + " on " + Repository.CurrentDate + Environment.NewLine + Environment.NewLine +
                                    "Reason given: " + reason + Environment.NewLine +
                                    "#Original Submission" + Environment.NewLine +
                                    "##" + submission.Title + Environment.NewLine +
                                    (submission.Type == 1 ?
                                        submission.Content
                                    :
                                    "[" + submission.Url + "](" + submission.Url + ")"
                                    )
                       
                    };
                    var cmd = new SendMessageCommand(message, isAnonymized: submission.IsAnonymized);
                    cmd.Execute();

                    // remove sticky if submission was stickied
                    var existingSticky = _db.StickiedSubmissions.FirstOrDefault(s => s.SubmissionID == submissionID);
                    if (existingSticky != null)
                    {
                        _db.StickiedSubmissions.Remove(existingSticky);
                    }

                    _db.SaveChanges();
                }
                else
                {
                    throw new VoatSecurityException("User doesn't have permission to delete submission.");
                }
            }

            return Selectors.SecureSubmission(submission);
        }

        private static string UserDeletedContentMessage()
        {
            return "Deleted by author at " + Repository.CurrentDate;
        }

        public async Task<CommandResponse> LogVisit(int submissionID, string clientIpAddress)
        {

            if (!String.IsNullOrEmpty(clientIpAddress))
            {
                try
                {

                    // generate salted hash of client IP address
                    string hash = IpHash.CreateHash(clientIpAddress);

                    // register a new session for this subverse
                    //New logic

                    var sql =
                        @"IF NOT EXISTS (
                    SELECT st.* FROM SessionTracker st WITH (NOLOCK)
                    WHERE st.SessionID = @SessionID AND st.Subverse = (SELECT TOP 1 Subverse FROM Submission WITH (NOLOCK) WHERE ID = @SubmissionID)
                    ) 
                    INSERT SessionTracker (SessionID, Subverse, CreationDate)
                    SELECT @SessionID, s.Subverse, getutcdate() FROM Submission s WITH (NOLOCK) WHERE ID = @SubmissionID";

                    await _db.Database.Connection.ExecuteAsync(sql, new { SessionID = hash, SubmissionID = submissionID }).ConfigureAwait(false);


                    //SessionHelper.Add(subverse.Name, hash);

                    //current logic
                    //if (SessionExists(sessionId, subverseName))
                    //    return;
                    //using (var db = new voatEntities())
                    //{
                    //    var newSession = new SessionTracker { SessionID = sessionId, Subverse = subverseName, CreationDate = Repository.CurrentDate };

                    //    db.SessionTrackers.Add(newSession);
                    //    db.SaveChanges();

                    //}

                    sql =
                        @"IF NOT EXISTS (
                        SELECT * FROM ViewStatistic vs WITH (NOLOCK) WHERE vs.SubmissionID = @SubmissionID AND vs.ViewerID = @SessionID
                        )
                        BEGIN 
                        INSERT ViewStatistic (SubmissionID, ViewerID) VALUES (@SubmissionID, @SessionID)
                        UPDATE Submission SET Views = (Views + 1) WHERE ID = @SubmissionID
                        END";

                    await _db.Database.Connection.ExecuteAsync(sql, new { SessionID = hash, SubmissionID = submissionID }).ConfigureAwait(false);


                    //// register a new view for this thread
                    //// check if this hash is present for this submission id in viewstatistics table
                    //var existingView = _db.ViewStatistics.Find(submissionID, hash);

                    //// this IP has already viwed this thread, skip registering a new view
                    //if (existingView == null)
                    //{
                    //    // this is a new view, register it for this submission
                    //    var view = new ViewStatistic { SubmissionID = submissionID, ViewerID = hash };
                    //    _db.ViewStatistics.Add(view);
                    //    submission.Views++;
                    //    await _db.SaveChangesAsync();
                    //}
                }
                catch (Exception ex)
                {
                    EventLogger.Instance.Log(ex);
                    throw ex;
                }

            }
            return CommandResponse.FromStatus(Status.Success, "");
        }

        #endregion Submissions

        #region Comments

        public async Task<IEnumerable<Domain.Models.SubmissionComment>> GetUserComments(string userName, SearchOptions options)
        {
            if (String.IsNullOrEmpty(userName))
            {
                throw new VoatValidationException("A user name must be provided.");
            }
            if (!UserHelper.UserExists(userName))
            {
                throw new VoatValidationException("User '{0}' does not exist.", userName);
            }

            var query = (from comment in _db.Comments
                         join submission in _db.Submissions on comment.SubmissionID equals submission.ID
                         where
                            !comment.IsAnonymized
                            && !comment.IsDeleted
                            && (comment.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
                         select new Domain.Models.SubmissionComment()
                         {
                             Submission = new SubmissionSummary() {
                                 Title = submission.Title,
                                 IsDeleted = submission.IsDeleted,
                                 IsAnonymized = submission.IsAnonymized,
                                 UserName = (submission.IsAnonymized || submission.IsDeleted ? "" : submission.UserName)
                             },
                             ID = comment.ID,
                             ParentID = comment.ParentID,
                             Content = comment.Content,
                             FormattedContent = comment.FormattedContent,
                             UserName = comment.UserName,
                             UpCount = (int)comment.UpCount,
                             DownCount = (int)comment.DownCount,
                             CreationDate = comment.CreationDate,
                             IsAnonymized = comment.IsAnonymized,
                             IsDeleted = comment.IsDeleted,
                             IsDistinguished = comment.IsDistinguished,
                             LastEditDate = comment.LastEditDate,
                             SubmissionID = comment.SubmissionID,
                             Subverse = submission.Subverse
                         });

            query = ApplyCommentSearch(options, query);
            var results = await query.ToListAsync().ConfigureAwait(false);

            return results;
        }

        public IEnumerable<Domain.Models.SubmissionComment> GetComments(string subverse, SearchOptions options)
        {
            var query = (from comment in _db.Comments
                         join submission in _db.Submissions on comment.SubmissionID equals submission.ID
                         where
                         !comment.IsDeleted
                         && (submission.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase) || String.IsNullOrEmpty(subverse))
                         select new Domain.Models.SubmissionComment()
                         {
                             Submission = new SubmissionSummary()
                             {
                                 Title = submission.Title,
                                 IsDeleted = submission.IsDeleted,
                                 IsAnonymized = submission.IsAnonymized,
                                 UserName = (submission.IsAnonymized || submission.IsDeleted ? "" : submission.UserName)
                             },
                             ID = comment.ID,
                             ParentID = comment.ParentID,
                             Content = comment.Content,
                             FormattedContent = comment.FormattedContent,
                             UserName = comment.UserName,
                             UpCount = (int)comment.UpCount,
                             DownCount = (int)comment.DownCount,
                             CreationDate = comment.CreationDate,
                             IsAnonymized = comment.IsAnonymized,
                             IsDeleted = comment.IsDeleted,
                             IsDistinguished = comment.IsDistinguished,
                             LastEditDate = comment.LastEditDate,
                             SubmissionID = comment.SubmissionID,
                             Subverse = submission.Subverse
                         });

            query = ApplyCommentSearch(options, query);
            var results = query.ToList();

            return results;
        }

        //This is the new process to retrieve comments.
        public IEnumerable<usp_CommentTree_Result> GetCommentTree(int submissionID, int? depth, int? parentID)
        {
            if (depth.HasValue && depth < 0)
            {
                depth = null;
            }
            var commentTree = _db.usp_CommentTree(submissionID, depth, parentID);
            var results = commentTree.ToList();
            return results;
        }
        //For backwards compat
        public async Task<Domain.Models.Comment> GetComment(int commentID)
        {
            var result = await GetComments(commentID);
            return result.FirstOrDefault();
        }
        public async Task<IEnumerable<Domain.Models.Comment>> GetComments(params int[] commentID)
        {

            var q = new DapperQuery();
            q.Select = "c.*, s.Subverse FROM Comment c WITH (NOLOCK) INNER JOIN Submission s WITH (NOLOCK) ON s.ID = c.SubmissionID";
            q.Where = "c.ID IN @IDs";

            q.Parameters = (new { IDs = commentID}).ToDynamicParameters();

            //var query = (from comment in _db.Comments
            //             join submission in _db.Submissions on comment.SubmissionID equals submission.ID
            //             where
            //             comment.ID == commentID
            //             select new Domain.Models.Comment()
            //             {
            //                 ID = comment.ID,
            //                 ParentID = comment.ParentID,
            //                 Content = comment.Content,
            //                 FormattedContent = comment.FormattedContent,
            //                 UserName = comment.UserName,
            //                 UpCount = (int)comment.UpCount,
            //                 DownCount = (int)comment.DownCount,
            //                 CreationDate = comment.CreationDate,
            //                 IsAnonymized = comment.IsAnonymized,
            //                 IsDeleted = comment.IsDeleted,
            //                 IsDistinguished = comment.IsDistinguished,
            //                 LastEditDate = comment.LastEditDate,
            //                 SubmissionID = comment.SubmissionID,
            //                 Subverse = submission.Subverse
            //             });

            //var record = query.FirstOrDefault();

            var data = await _db.Database.Connection.QueryAsync<Domain.Models.Comment>(q.ToString(), q.Parameters);

            DomainMaps.HydrateUserData(data);

            return data;
        }

        private async Task ResetVotes(ContentType contentType, int id, Vote voteStatus, Vote voteValue)
        {
            var u = new DapperUpdate();
            switch (contentType)
            {
                case ContentType.Comment:
                    u.Update = @"UPDATE v SET v.VoteValue = @VoteValue FROM CommentVoteTracker v
                                INNER JOIN Comment c WITH (NOLOCK) ON c.ID = v.CommentID
                                INNER JOIN Submission s WITH (NOLOCK) ON c.SubmissionID = s.ID";
                    u.Where = "v.CommentID = @ID AND v.VoteStatus = @VoteStatus AND s.ArchiveDate IS NULL";
                    break;
                default:
                    throw new NotImplementedException($"Method not implemented for ContentType: {contentType.ToString()}");
                    break;
            }
            int count = await _db.Database.Connection.ExecuteAsync(u.ToString(), new { ID = id, VoteStatus = (int)voteStatus, VoteValue = (int)voteValue });
        }

        public async Task<CommandResponse<Data.Models.Comment>> DeleteComment(int commentID, string reason = null)
        {
            DemandAuthentication();

            var comment = _db.Comments.Find(commentID);

            if (comment != null && !comment.IsDeleted)
            {
                var submission = _db.Submissions.Find(comment.SubmissionID);
                if (submission != null)
                {
                    var subverseName = submission.Subverse;

                    // delete comment if the comment author is currently logged in user
                    if (comment.UserName == User.Identity.Name)
                    {
                        //User Deletion
                        comment.IsDeleted = true;
                        comment.Content = UserDeletedContentMessage();
                        comment.FormattedContent = Formatting.FormatMessage(comment.Content);
                        await _db.SaveChangesAsync().ConfigureAwait(false);

                        //User Deletions remove UpVoted CCP - This is one way ccp farmers accomplish their acts
                        if (comment.UpCount > comment.DownCount)
                        {
                            await ResetVotes(ContentType.Comment, comment.ID, Vote.Up, Vote.None).ConfigureAwait(false);
                        }
                    }

                    // delete comment if delete request is issued by subverse moderator
                    else if (ModeratorPermission.HasPermission(User.Identity.Name, submission.Subverse, ModeratorAction.DeleteComments))
                    {
                        if (String.IsNullOrEmpty(reason))
                        {
                            var ex = new VoatValidationException("A reason for deletion is required");
                            ex.Data["CommentID"] = commentID;
                            throw ex;
                        }
                        var contentPath = VoatPathHelper.CommentsPagePath(submission.Subverse, comment.SubmissionID.Value, comment.ID);

                        // notify comment author that his comment has been deleted by a moderator
                        var message = new Domain.Models.SendMessage()
                        {
                            Sender = $"v/{subverseName}",
                            Recipient = comment.UserName,
                            Subject = $"Comment {contentPath} deleted",
                            Message = "Your comment [" + contentPath + "](" + contentPath + ") has been deleted by: " +
                                        "@" + User.Identity.Name + " on: " + Repository.CurrentDate + Environment.NewLine + Environment.NewLine +
                                        "Reason given: " + reason + Environment.NewLine +
                                        "#Original Comment" + Environment.NewLine +
                                        comment.Content
                        };
                        var cmd = new SendMessageCommand(message, isAnonymized: comment.IsAnonymized);
                        await cmd.Execute().ConfigureAwait(false);

                        comment.IsDeleted = true;

                        // move the comment to removal log
                        var removalLog = new Data.Models.CommentRemovalLog
                        {
                            CommentID = comment.ID,
                            Moderator = User.Identity.Name,
                            Reason = reason,
                            CreationDate = Repository.CurrentDate
                        };

                        _db.CommentRemovalLogs.Add(removalLog);

                        await _db.SaveChangesAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        var ex = new VoatSecurityException("User doesn't have permissions to perform requested action");
                        ex.Data["CommentID"] = commentID;
                        throw ex;
                    }
                }
            }
            return CommandResponse.Successful(Selectors.SecureComment(comment));
        }

        [Authorize]
        public async Task<CommandResponse<Data.Models.Comment>> EditComment(int commentID, string content)
        {
            DemandAuthentication();

            var comment = _db.Comments.Find(commentID);

            if (comment != null)
            {
                if (comment.UserName != User.Identity.Name)
                {
                    return CommandResponse.FromStatus((Data.Models.Comment)null, Status.Denied, "User doesn't have permissions to perform requested action");
                }

                //evaluate rule
                VoatRuleContext context = new VoatRuleContext();

                //set any state we have so context doesn't have to retrieve
                context.SubmissionID = comment.SubmissionID;
                context.PropertyBag.CommentContent = content;
                context.PropertyBag.Comment = comment;

                var outcome = VoatRulesEngine.Instance.EvaluateRuleSet(context, RuleScope.EditComment);

                if (outcome.IsAllowed)
                {
                    comment.LastEditDate = CurrentDate;
                    comment.Content = content;

                    if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPreSave))
                    {
                        comment.Content = ContentProcessor.Instance.Process(comment.Content, ProcessingStage.InboundPreSave, comment);
                    }

                    var formattedComment = Voat.Utilities.Formatting.FormatMessage(comment.Content);
                    comment.FormattedContent = formattedComment;

                    await _db.SaveChangesAsync().ConfigureAwait(false);
                }
                else
                {
                    return MapRuleOutCome<Data.Models.Comment>(outcome, null);
                }
            }
            else
            {
                throw new VoatNotFoundException("Can not find comment with ID {0}", commentID);
            }

            return CommandResponse.Successful(Selectors.SecureComment(comment));
        }

        public async Task<CommandResponse<Domain.Models.Comment>> PostCommentReply(int parentCommentID, string comment)
        {
            var c = _db.Comments.Find(parentCommentID);
            if (c == null)
            {
                throw new VoatNotFoundException("Can not find parent comment with id {0}", parentCommentID.ToString());
            }
            var submissionid = c.SubmissionID;
            return await PostComment(submissionid.Value, parentCommentID, comment).ConfigureAwait(false);
        }

        public async Task<CommandResponse<Domain.Models.Comment>> PostComment(int submissionID, int? parentCommentID, string commentContent)
        {
            DemandAuthentication();

            var submission = GetSubmissionUnprotected(submissionID);
            if (submission == null)
            {
                throw new VoatNotFoundException("submissionID", submissionID, "Can not find submission");
            }

            //evaluate rule
            VoatRuleContext context = new VoatRuleContext();

            //set any state we have so context doesn't have to retrieve
            context.SubmissionID = submissionID;
            context.PropertyBag.CommentContent = commentContent;

            var outcome = VoatRulesEngine.Instance.EvaluateRuleSet(context, RuleScope.Post, RuleScope.PostComment);

            if (outcome.IsAllowed)
            {
                //Save comment
                var c = new Models.Comment();
                c.CreationDate = Repository.CurrentDate;
                c.UserName = User.Identity.Name;
                c.ParentID = (parentCommentID > 0 ? parentCommentID : (int?)null);
                c.SubmissionID = submissionID;
                c.UpCount = 0;

                //TODO: Ensure this is acceptable
                //c.IsAnonymized = (submission.IsAnonymized || subverse.IsAnonymized);
                c.IsAnonymized = submission.IsAnonymized;

                c.Content = ContentProcessor.Instance.Process(commentContent, ProcessingStage.InboundPreSave, c);

                //save fully formatted content
                var formattedComment = Formatting.FormatMessage(c.Content);
                c.FormattedContent = formattedComment;

                _db.Comments.Add(c);
                await _db.SaveChangesAsync().ConfigureAwait(false);

                if (ContentProcessor.Instance.HasStage(ProcessingStage.InboundPostSave))
                {
                    ContentProcessor.Instance.Process(c.Content, ProcessingStage.InboundPostSave, c);
                }

                await NotificationManager.SendCommentNotification(submission, c).ConfigureAwait(false);

                return MapRuleOutCome(outcome, DomainMaps.Map(Selectors.SecureComment(c), submission.Subverse));
            }

            return MapRuleOutCome(outcome, (Domain.Models.Comment)null);
        }

        #endregion Comments

        #region Api

        public bool IsApiKeyValid(string apiPublicKey)
        {
            var key = GetApiKey(apiPublicKey);

            if (key != null && key.IsActive)
            {
                //TODO: This needs to be non-blocking and non-queued. If 20 threads with same apikey are accessing this method at once we don't want to perform 20 updates on record.
                //keep track of last access date
                key.LastAccessDate = CurrentDate;
                _db.SaveChanges();

                return true;
            }

            return false;
        }

        public ApiClient GetApiKey(string apiPublicKey)
        {
            var result = (from x in this._db.ApiClients
                          where x.PublicKey == apiPublicKey
                          select x).FirstOrDefault();
            return result;
        }

        [Authorize]
        public IEnumerable<ApiClient> GetApiKeys(string userName)
        {
            var result = from x in this._db.ApiClients
                         where x.UserName == userName
                         orderby x.IsActive descending, x.CreationDate descending
                         select x;
            return result.ToList();
        }

        [Authorize]
        public ApiThrottlePolicy GetApiThrottlePolicy(int throttlePolicyID)
        {
            var result = from policy in _db.ApiThrottlePolicies
                         where policy.ID == throttlePolicyID
                         select policy;

            return result.FirstOrDefault();
        }

        [Authorize]
        public ApiPermissionPolicy GetApiPermissionPolicy(int permissionPolicyID)
        {
            var result = from policy in _db.ApiPermissionPolicies
                         where policy.ID == permissionPolicyID
                         select policy;

            return result.FirstOrDefault();
        }

        [Authorize]
        public List<KeyValuePair<string, string>> GetApiClientKeyThrottlePolicies()
        {
            List<KeyValuePair<string, string>> policies = new List<KeyValuePair<string, string>>();

            var result = from client in this._db.ApiClients
                         join policy in _db.ApiThrottlePolicies on client.ApiThrottlePolicyID equals policy.ID
                         where client.IsActive == true
                         select new { client.PublicKey, policy.Policy };

            foreach (var policy in result)
            {
                policies.Add(new KeyValuePair<string, string>(policy.PublicKey, policy.Policy));
            }

            return policies;
        }

        public async Task<ApiClient> EditApiKey(string apiKey, string name, string description, string url, string redirectUrl)
        {
            DemandAuthentication();

            //Only allow users to edit ApiKeys if they IsActive == 1 and Current User owns it.
            var apiClient = (from x in _db.ApiClients
                          where x.PublicKey == apiKey && x.UserName == User.Identity.Name && x.IsActive == true
                          select x).FirstOrDefault();

            if (apiClient != null)
            {
                apiClient.AppAboutUrl = url;
                apiClient.RedirectUrl = redirectUrl;
                apiClient.AppDescription = description;
                apiClient.AppName = name;
                await _db.SaveChangesAsync().ConfigureAwait(false);
            }

            return apiClient;
            
        }

        [Authorize]
        public void CreateApiKey(string name, string description, string url, string redirectUrl)
        {
            DemandAuthentication();

            ApiClient c = new ApiClient();
            c.IsActive = true;
            c.AppAboutUrl = url;
            c.RedirectUrl = redirectUrl;
            c.AppDescription = description;
            c.AppName = name;
            c.UserName = User.Identity.Name;
            c.CreationDate = CurrentDate;

            var generatePublicKey = new Func<string>(() =>
            {
                return String.Format("VO{0}AT", Guid.NewGuid().ToString().Replace("-", "").ToUpper());
            });

            //just make sure key isn't already in db
            var publicKey = generatePublicKey();
            while (_db.ApiClients.Any(x => x.PublicKey == publicKey))
            {
                publicKey = generatePublicKey();
            }

            c.PublicKey = publicKey;
            c.PrivateKey = (Guid.NewGuid().ToString() + Guid.NewGuid().ToString()).Replace("-", "").ToUpper();

            //Using OAuth 2, we don't need enc keys
            //var keyGen = RandomNumberGenerator.Create();
            //byte[] tempKey = new byte[16];
            //keyGen.GetBytes(tempKey);
            //c.PublicKey = Convert.ToBase64String(tempKey);

            //tempKey = new byte[64];
            //keyGen.GetBytes(tempKey);
            //c.PrivateKey = Convert.ToBase64String(tempKey);

            _db.ApiClients.Add(c);
            _db.SaveChanges();
        }

        [Authorize]
        public ApiClient DeleteApiKey(int id)
        {
            DemandAuthentication();

            //Only allow users to delete ApiKeys if they IsActive == 1
            var apiKey = (from x in _db.ApiClients
                          where x.ID == id && x.UserName == User.Identity.Name && x.IsActive == true
                          select x).FirstOrDefault();

            if (apiKey != null)
            {
                apiKey.IsActive = false;
                _db.SaveChanges();
            }
            return apiKey;
        }

        public IEnumerable<ApiCorsPolicy> GetApiCorsPolicies()
        {
            var policy = (from x in _db.ApiCorsPolicies
                          where
                          x.IsActive
                          select x).ToList();
            return policy;
        }

        public ApiCorsPolicy GetApiCorsPolicy(string origin)
        {
            var domain = origin;

            //Match and pull domain only
            var domainMatch = Regex.Match(origin, @"://(?<domainPort>(?<domain>[\w.-]+)(?::\d+)?)[/]?");
            if (domainMatch.Success)
            {
                domain = domainMatch.Groups["domain"].Value;

                //var domain = domainMatch.Groups["domainPort"];
            }

            var policy = (from x in _db.ApiCorsPolicies

                              //haven't decided exactly how we are going to store origin (i.e. just the doamin name, with/without protocol, etc.)
                          where
                          (x.AllowOrigin.Equals(origin, StringComparison.OrdinalIgnoreCase)
                          || x.AllowOrigin.Equals(domain, StringComparison.OrdinalIgnoreCase))
                          && x.IsActive
                          select x).FirstOrDefault();
            return policy;
        }

        public void SaveApiLogEntry(ApiLog logentry)
        {
            logentry.CreationDate = CurrentDate;
            _db.ApiLogs.Add(logentry);
            _db.SaveChanges();
        }

        public void UpdateApiClientLastAccessDate(int apiClientID)
        {
            var client = _db.ApiClients.Where(x => x.ID == apiClientID).FirstOrDefault();
            client.LastAccessDate = CurrentDate;
            _db.SaveChanges();
        }

        #endregion Api

        #region UserMessages

        public async Task<IEnumerable<int>> GetUserSavedItems(ContentType type, string userName)
        {
            List<int> savedIDs = null;
            switch (type)
            {
                case ContentType.Comment:
                    savedIDs = await _db.CommentSaveTrackers.Where(x => x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)).Select(x => x.CommentID).ToListAsync().ConfigureAwait(false);
                    break;
                case ContentType.Submission:
                    savedIDs = await _db.SubmissionSaveTrackers.Where(x => x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)).Select(x => x.SubmissionID).ToListAsync().ConfigureAwait(false);
                    break;
            }
            return savedIDs;
        }

        /// <summary>
        /// Save Comments and Submissions toggle.
        /// </summary>
        /// <param name="type">The type of content in which to save</param>
        /// <param name="ID">The ID of the item in which to save</param>
        /// <param name="forceAction">Forces the Save function to operate as a Save only or Unsave only rather than a toggle. If true, will only save if it hasn't been previously saved, if false, will only remove previous saved entry, if null (default) will function as a toggle.</param>
        /// <returns>The end result if the item is saved or not. True if saved, false if not saved.</returns>
        public async Task<CommandResponse<bool?>> Save(ContentType type, int ID, bool? forceAction = null)
        {
            //TODO: These save trackers should be stored in a single table in SQL. Two tables for such similar information isn't ideal... mmkay. Makes querying nasty.
            //TODO: There is a potential issue with this code. There is no validation that the ID belongs to a comment or a submission. This is nearly impossible to determine anyways but it's still an issue.
            string currentUserName = User.Identity.Name;
            bool isSaved = false;

            switch (type)
            {
                case ContentType.Comment:

                    var c = _db.CommentSaveTrackers.FirstOrDefault(x => x.CommentID == ID && x.UserName.Equals(currentUserName, StringComparison.OrdinalIgnoreCase));

                    if (c == null && (forceAction == null || forceAction.HasValue && forceAction.Value))
                    {
                        c = new CommentSaveTracker() { CommentID = ID, UserName = currentUserName, CreationDate = CurrentDate };
                        _db.CommentSaveTrackers.Add(c);
                        isSaved = true;
                    }
                    else if (c != null && (forceAction == null || forceAction.HasValue && !forceAction.Value))
                    {
                        _db.CommentSaveTrackers.Remove(c);
                        isSaved = false;
                    }
                    await _db.SaveChangesAsync().ConfigureAwait(false);

                    break;

                case ContentType.Submission:

                    var s = _db.SubmissionSaveTrackers.FirstOrDefault(x => x.SubmissionID == ID && x.UserName.Equals(currentUserName, StringComparison.OrdinalIgnoreCase));
                    if (s == null && (forceAction == null || forceAction.HasValue && forceAction.Value))
                    {
                        s = new SubmissionSaveTracker() { SubmissionID = ID, UserName = currentUserName, CreationDate = CurrentDate };
                        _db.SubmissionSaveTrackers.Add(s);
                        isSaved = true;
                    }
                    else if (s != null && (forceAction == null || forceAction.HasValue && !forceAction.Value))
                    {
                        _db.SubmissionSaveTrackers.Remove(s);
                        isSaved = false;
                    }
                    await _db.SaveChangesAsync().ConfigureAwait(false);

                    break;
            }

            return CommandResponse.FromStatus<bool?>(forceAction.HasValue ? forceAction.Value : isSaved, Status.Success, "");
        }

        public static void SetDefaultUserPreferences(Data.Models.UserPreference p)
        {
            p.Language = "en";
            p.NightMode = false;
            p.OpenInNewWindow = false;
            p.UseSubscriptionsMenu = true;
            p.DisableCSS = false;
            p.DisplaySubscriptions = false;
            p.DisplayVotes = false;
            p.EnableAdultContent = false;
            p.Bio = null;
            p.Avatar = null;
            p.CollapseCommentLimit = 4;
            p.DisplayCommentCount = 5;
            p.HighlightMinutes = 30;
            p.VanityTitle = null;
        }

        [Authorize]
        public void SaveUserPrefernces(Domain.Models.UserPreference preferences)
        {
            DemandAuthentication();

            var p = (from x in _db.UserPreferences
                     where x.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase)
                     select x).FirstOrDefault();

            if (p == null)
            {
                p = new Data.Models.UserPreference();
                p.UserName = User.Identity.Name;
                SetDefaultUserPreferences(p);
                _db.UserPreferences.Add(p);
            }

            if (!String.IsNullOrEmpty(preferences.Bio))
            {
                p.Bio = preferences.Bio;
            }
            if (!String.IsNullOrEmpty(preferences.Language))
            {
                p.Language = preferences.Language;
            }
            if (preferences.OpenInNewWindow.HasValue)
            {
                p.OpenInNewWindow = preferences.OpenInNewWindow.Value;
            }
            if (preferences.DisableCSS.HasValue)
            {
                p.DisableCSS = preferences.DisableCSS.Value;
            }
            if (preferences.EnableAdultContent.HasValue)
            {
                p.EnableAdultContent = preferences.EnableAdultContent.Value;
            }
            if (preferences.NightMode.HasValue)
            {
                p.NightMode = preferences.NightMode.Value;
            }
            if (preferences.DisplaySubscriptions.HasValue)
            {
                p.DisplaySubscriptions = preferences.DisplaySubscriptions.Value;
            }
            if (preferences.DisplayVotes.HasValue)
            {
                p.DisplayVotes = preferences.DisplayVotes.Value;
            }
            if (preferences.UseSubscriptionsMenu.HasValue)
            {
                p.UseSubscriptionsMenu = preferences.UseSubscriptionsMenu.Value;
            }
            if (preferences.DisplayCommentCount.HasValue)
            {
                p.DisplayCommentCount = preferences.DisplayCommentCount.Value;
            }
            if (preferences.HighlightMinutes.HasValue)
            {
                p.HighlightMinutes = preferences.HighlightMinutes.Value;
            }
            if (!String.IsNullOrEmpty(preferences.VanityTitle))
            {
                p.VanityTitle = preferences.VanityTitle;
            }
            if (preferences.CollapseCommentLimit.HasValue)
            {
                p.CollapseCommentLimit = preferences.CollapseCommentLimit.Value;
            }
            if (preferences.DisplayAds.HasValue)
            {
                p.DisplayAds = preferences.DisplayAds.Value;
            }
            _db.SaveChanges();
        }

        [Authorize]
        public async Task<CommandResponse<Domain.Models.Message>> SendMessageReply(int id, string messageContent)
        {
            DemandAuthentication();

            var userName = User.Identity.Name;

            var m = (from x in _db.Messages
                     where x.ID == id
                     select x).FirstOrDefault();

            if (m == null)
            {
                return new CommandResponse<Domain.Models.Message>(null, Status.NotProcessed, "Couldn't find message in which to reply");
            }
            else
            {
                var message = new Domain.Models.Message();
                CommandResponse<Domain.Models.Message> commandResponse = null;

                //determine if message replying to is a comment and if so execute a comment reply
                switch ((MessageType)m.Type)
                {
                    case MessageType.CommentMention:
                    case MessageType.CommentReply:
                    case MessageType.SubmissionReply:
                    case MessageType.SubmissionMention:

                        Domain.Models.Comment comment;
                        //assume every comment type has a submission ID contained in it
                        var cmd = new CreateCommentCommand(m.SubmissionID.Value, m.CommentID, messageContent);
                        var response = await cmd.Execute();

                        if (response.Success)
                        {
                            comment = response.Response;
                            commandResponse = CommandResponse.Successful(new Domain.Models.Message()
                            {
                                ID = -1,
                                Comment = comment,
                                SubmissionID = comment.SubmissionID,
                                CommentID = comment.ID,
                                Content = comment.Content,
                                FormattedContent = comment.FormattedContent,
                            });
                        }
                        else
                        {
                            commandResponse = CommandResponse.FromStatus<Domain.Models.Message>(null, response.Status, response.Message);
                        }

                        break;
                    case MessageType.Sent:
                        //no replying to sent messages
                        commandResponse = CommandResponse.FromStatus<Domain.Models.Message>(null, Status.Denied, "Sent messages do not allow replies");
                        break;
                    default:

                        if (m.RecipientType == (int)IdentityType.Subverse)
                        {
                            if (!ModeratorPermission.HasPermission(User.Identity.Name, m.Recipient, ModeratorAction.SendMail))
                            {
                                commandResponse = new CommandResponse<Domain.Models.Message>(null, Status.NotProcessed, "Message integrity violated");
                            }

                            message.Recipient = m.Sender;
                            message.RecipientType = (IdentityType)m.SenderType;

                            message.Sender = m.Recipient;
                            message.SenderType = (IdentityType)m.RecipientType;
                        }
                        else
                        {
                            message.Recipient = m.Sender;
                            message.RecipientType = (IdentityType)m.SenderType;

                            message.Sender = m.Recipient;
                            message.SenderType = (IdentityType)m.RecipientType;
                        }

                        message.ParentID = m.ID;
                        message.CorrelationID = m.CorrelationID;
                        message.Title = m.Title;
                        message.Content = messageContent;
                        message.FormattedContent = Formatting.FormatMessage(messageContent);
                        message.IsAnonymized = m.IsAnonymized;
                        commandResponse = await SendMessage(message).ConfigureAwait(false);

                        break;
                }
                //return response
                return commandResponse;
            }
        }

        [Authorize]
        public async Task<IEnumerable<CommandResponse<Domain.Models.Message>>> SendMessages(params Domain.Models.Message[] messages)
        {
            return await SendMessages(messages.AsEnumerable()).ConfigureAwait(false);
        }

        [Authorize]
        public async Task<IEnumerable<CommandResponse<Domain.Models.Message>>> SendMessages(IEnumerable<Domain.Models.Message> messages)
        {
            var tasks = messages.Select(x => Task.Run(async () => { return await SendMessage(x).ConfigureAwait(false); }));

            var result = await Task.WhenAll(tasks).ConfigureAwait(false);

            return result;
        }


        /// <summary>
        /// Main SendMessage routine.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        [Authorize]
        public async Task<CommandResponse<Domain.Models.Message>> SendMessage(Domain.Models.Message message)
        {
            DemandAuthentication();

            using (var db = new voatEntities())
            {
                try
                {
                    List<Domain.Models.Message> messages = new List<Domain.Models.Message>();

                    //increased subject line
                    int max = 500;
                    message.CreatedBy = User.Identity.Name;
                    message.Title = message.Title.SubstringMax(max);
                    message.CreationDate = CurrentDate;
                    message.FormattedContent = Formatting.FormatMessage(message.Content);

                    if (!MesssagingUtility.IsSenderBlocked(message.Sender, message.Recipient))
                    {
                        messages.Add(message);
                    }

                    if (message.Type == MessageType.Private)
                    {
                        //add sent copy
                        var sentCopy = message.Clone();
                        sentCopy.Type = MessageType.Sent;
                        sentCopy.ReadDate = CurrentDate;
                        messages.Add(sentCopy);
                    }

                    var mappedDataMessages = messages.Map();
                    var addedMessages = db.Messages.AddRange(mappedDataMessages);
                    await db.SaveChangesAsync().ConfigureAwait(false);

                    //send notices async
                    Task.Run(() => EventNotification.Instance.SendMessageNotice(
                        UserDefinition.Format(message.Recipient, message.RecipientType),
                        UserDefinition.Format(message.Sender, message.SenderType),
                        Domain.Models.MessageTypeFlag.Private,
                        null,
                        null,
                        message.Content));

                    return CommandResponse.Successful(addedMessages.First().Map());
                }
                catch (Exception ex)
                {
                    //TODO Log this
                    return CommandResponse.Error<CommandResponse<Domain.Models.Message>>(ex);
                }
            }
        }

        [Authorize]
        public async Task<CommandResponse<Domain.Models.Message>> SendMessage(SendMessage message, bool forceSend = false, bool ensureUserExists = true, bool isAnonymized = false)
        {
            DemandAuthentication();

            Domain.Models.Message responseMessage = null;

            var sender = UserDefinition.Parse(message.Sender);

            //If sender isn't a subverse (automated messages) run sender checks
            if (sender.Type == IdentityType.Subverse)
            {
                var subverse = sender.Name;
                if (!ModeratorPermission.HasPermission(User.Identity.Name, subverse, ModeratorAction.SendMail))
                {
                    return CommandResponse.FromStatus(responseMessage, Status.Denied, "User not allowed to send mail from subverse");
                }
            }
            else
            {
                //Sender can be passed in from the UI , ensure it is replaced here
                message.Sender = User.Identity.Name;

                if (Voat.Utilities.BanningUtility.ContentContainsBannedDomain(null, message.Message))
                {
                    return CommandResponse.FromStatus(responseMessage, Status.Ignored, "Message contains banned domain");
                }
                if (Voat.Utilities.UserHelper.IsUserGloballyBanned(message.Sender))
                {
                    return CommandResponse.FromStatus(responseMessage, Status.Ignored, "User is banned");
                }
                var userData = new UserData(message.Sender);
                //add exception for system messages from sender
                if (!forceSend && !CONSTANTS.SYSTEM_USER_NAME.Equals(message.Sender, StringComparison.OrdinalIgnoreCase) && userData.Information.CommentPoints.Sum < 10)
                {
                    return CommandResponse.FromStatus(responseMessage, Status.Ignored, "Comment points too low to send messages. Need at least 10 CCP.");
                }
            }

            List<Domain.Models.Message> messages = new List<Domain.Models.Message>();

            var userDefinitions = UserDefinition.ParseMany(message.Recipient);
            if (userDefinitions.Count() <= 0)
            {
                return CommandResponse.FromStatus(responseMessage, Status.NotProcessed, "No recipient specified");
            }

            foreach (var def in userDefinitions)
            {
                if (def.Type == IdentityType.Subverse)
                {
                    messages.Add(new Domain.Models.Message
                    {
                        CorrelationID = Domain.Models.Message.NewCorrelationID(),
                        Sender = sender.Name,
                        SenderType = sender.Type,
                        Recipient = def.Name,
                        RecipientType = IdentityType.Subverse,
                        Title = message.Subject,
                        Content = message.Message,
                        ReadDate = null,
                        CreationDate = Repository.CurrentDate,
                        IsAnonymized = isAnonymized,
                    });
                }
                else
                {
                    //ensure proper cased, will return null if doesn't exist
                    var recipient = UserHelper.OriginalUsername(def.Name);
                    if (String.IsNullOrEmpty(recipient))
                    {
                        if (ensureUserExists)
                        {
                            return CommandResponse.FromStatus<Domain.Models.Message>(null, Status.Error, $"User {recipient} does not exist.");
                        }
                    }
                    else
                    {
                        messages.Add(new Domain.Models.Message
                        {
                            CorrelationID = Domain.Models.Message.NewCorrelationID(),
                            Sender = sender.Name,
                            SenderType = sender.Type,
                            Recipient = recipient,
                            RecipientType = IdentityType.User,
                            Title = message.Subject,
                            Content = message.Message,
                            ReadDate = null,
                            CreationDate = Repository.CurrentDate,
                            IsAnonymized = isAnonymized,
                        });
                    }
                }
            }

            var savedMessages = await SendMessages(messages).ConfigureAwait(false);
            var firstSent = savedMessages.FirstOrDefault();
            if (firstSent == null)
            {
                firstSent = CommandResponse.FromStatus((Domain.Models.Message)null, Status.Success, "");
            }
            return firstSent;
        }

        [Obsolete("Packing up and moving to Dapper", true)]
        private IQueryable<Data.Models.Message> GetMessageQueryBase(string ownerName, IdentityType ownerType, MessageTypeFlag type, MessageState state)
        {
            return GetMessageQueryBase(_db, ownerName, ownerType, type, state);
        }
        private DapperQuery GetMessageQueryDapperBase(string ownerName, IdentityType ownerType, MessageTypeFlag type, MessageState state)
        {
            var q = new DapperQuery();

            q.Select = "SELECT {0} FROM [Message] m WITH (NOLOCK) LEFT JOIN Submission s WITH (NOLOCK) ON s.ID = m.SubmissionID LEFT JOIN Comment c WITH (NOLOCK) ON c.ID = m.CommentID";
            q.SelectColumns = "*";
            string senderClause = "";

            //messages include sent items, add special condition to include them
            if ((type & MessageTypeFlag.Sent) > 0)
            {
                senderClause = $" OR (m.Sender = @OwnerName AND m.SenderType = @OwnerType AND m.[Type] = {((int)MessageType.Sent).ToString()})";
            }
            q.Where = $"((m.Recipient = @OwnerName AND m.RecipientType = @OwnerType AND m.[Type] != {((int)MessageType.Sent).ToString()}){senderClause})";
            q.OrderBy = "m.CreationDate DESC";
            q.Parameters.Add("OwnerName", ownerName);
            q.Parameters.Add("OwnerType", (int)ownerType);

            //var q = (from m in context.Messages
            //             //join s in _db.Submissions on m.SubmissionID equals s.ID into ns
            //             //from s in ns.DefaultIfEmpty()
            //             //join c in _db.Comments on m.CommentID equals c.ID into cs
            //             //from c in cs.DefaultIfEmpty()
            //         where (
            //            (m.Recipient.Equals(ownerName, StringComparison.OrdinalIgnoreCase) && m.RecipientType == (int)ownerType && m.Type != (int)MessageType.Sent)
            //            ||
            //            //Limit sent messages
            //            (m.Sender.Equals(ownerName, StringComparison.OrdinalIgnoreCase) && m.SenderType == (int)ownerType && ((type & MessageTypeFlag.Sent) > 0) && m.Type == (int)MessageType.Sent)
            //         )
            //         select m);

            switch (state)
            {
                case MessageState.Read:
                    q.Append(x => x.Where,  "m.ReadDate IS NOT NULL");
                    break;
                case MessageState.Unread:
                    q.Append(x => x.Where, "m.ReadDate IS NULL");
                    break;
            }

            //filter Message Type
            if (type != MessageTypeFlag.All)
            {
                List<int> messageTypes = new List<int>();

                var flags = Enum.GetValues(typeof(MessageTypeFlag));
                foreach (var flag in flags)
                {
                    //This needs to be cleaned up, we have two enums that are serving a similar purpose
                    var mTFlag = (MessageTypeFlag)flag;
                    if (mTFlag != MessageTypeFlag.All && ((type & mTFlag) > 0))
                    {
                        switch (mTFlag)
                        {
                            case MessageTypeFlag.Sent:
                                messageTypes.Add((int)MessageType.Sent);
                                break;

                            case MessageTypeFlag.Private:
                                messageTypes.Add((int)MessageType.Private);
                                break;

                            case MessageTypeFlag.CommentReply:
                                messageTypes.Add((int)MessageType.CommentReply);
                                break;

                            case MessageTypeFlag.CommentMention:
                                messageTypes.Add((int)MessageType.CommentMention);
                                break;

                            case MessageTypeFlag.SubmissionReply:
                                messageTypes.Add((int)MessageType.SubmissionReply);
                                break;

                            case MessageTypeFlag.SubmissionMention:
                                messageTypes.Add((int)MessageType.SubmissionMention);
                                break;
                        }
                    }
                }
                q.Append(x => x.Where, "m.[Type] IN @Types");
                q.Parameters.Add("Types", messageTypes.ToArray());
                //q = q.Where(x => messageTypes.Contains(x.Type));
            }
            return q;
        }

        [Obsolete("Packing up and moving to Dapper", true)]
        private IQueryable<Data.Models.Message> GetMessageQueryBase(voatEntities context, string ownerName, IdentityType ownerType, MessageTypeFlag type, MessageState state)
        {
            var q = (from m in context.Messages
                         //join s in _db.Submissions on m.SubmissionID equals s.ID into ns
                         //from s in ns.DefaultIfEmpty()
                         //join c in _db.Comments on m.CommentID equals c.ID into cs
                         //from c in cs.DefaultIfEmpty()
                     where (
                        (m.Recipient.Equals(ownerName, StringComparison.OrdinalIgnoreCase) && m.RecipientType == (int)ownerType && m.Type != (int)MessageType.Sent)
                        ||
                        //Limit sent messages
                        (m.Sender.Equals(ownerName, StringComparison.OrdinalIgnoreCase) && m.SenderType == (int)ownerType && ((type & MessageTypeFlag.Sent) > 0) && m.Type == (int)MessageType.Sent)
                     )
                     select m);

            switch (state)
            {
                case MessageState.Read:
                    q = q.Where(x => x.ReadDate != null);
                    break;

                case MessageState.Unread:
                    q = q.Where(x => x.ReadDate == null);
                    break;
            }

            //filter Message Type
            if (type != MessageTypeFlag.All)
            {
                List<int> messageTypes = new List<int>();

                var flags = Enum.GetValues(typeof(MessageTypeFlag));
                foreach (var flag in flags)
                {
                    //This needs to be cleaned up, we have two enums that are serving a similar purpose
                    var mTFlag = (MessageTypeFlag)flag;
                    if (mTFlag != MessageTypeFlag.All && ((type & mTFlag) > 0))
                    {
                        switch (mTFlag)
                        {
                            case MessageTypeFlag.Sent:
                                messageTypes.Add((int)MessageType.Sent);
                                break;

                            case MessageTypeFlag.Private:
                                messageTypes.Add((int)MessageType.Private);
                                break;

                            case MessageTypeFlag.CommentReply:
                                messageTypes.Add((int)MessageType.CommentReply);
                                break;

                            case MessageTypeFlag.CommentMention:
                                messageTypes.Add((int)MessageType.CommentMention);
                                break;

                            case MessageTypeFlag.SubmissionReply:
                                messageTypes.Add((int)MessageType.SubmissionReply);
                                break;

                            case MessageTypeFlag.SubmissionMention:
                                messageTypes.Add((int)MessageType.SubmissionMention);
                                break;
                        }
                    }
                }
                q = q.Where(x => messageTypes.Contains(x.Type));
            }

            return q;
        }

        private List<int> ConvertMessageTypeFlag(MessageTypeFlag type)
        {
            //filter Message Type
            if (type != MessageTypeFlag.All)
            {
                List<int> messageTypes = new List<int>();

                var flags = Enum.GetValues(typeof(MessageTypeFlag));
                foreach (var flag in flags)
                {
                    //This needs to be cleaned up, we have two enums that are serving a similar purpose
                    var mTFlag = (MessageTypeFlag)flag;
                    if (mTFlag != MessageTypeFlag.All && ((type & mTFlag) > 0))
                    {
                        switch (mTFlag)
                        {
                            case MessageTypeFlag.Sent:
                                messageTypes.Add((int)MessageType.Sent);
                                break;

                            case MessageTypeFlag.Private:
                                messageTypes.Add((int)MessageType.Private);
                                break;

                            case MessageTypeFlag.CommentReply:
                                messageTypes.Add((int)MessageType.CommentReply);
                                break;

                            case MessageTypeFlag.CommentMention:
                                messageTypes.Add((int)MessageType.CommentMention);
                                break;

                            case MessageTypeFlag.SubmissionReply:
                                messageTypes.Add((int)MessageType.SubmissionReply);
                                break;

                            case MessageTypeFlag.SubmissionMention:
                                messageTypes.Add((int)MessageType.SubmissionMention);
                                break;
                        }
                    }
                }
                return messageTypes;
            }
            else
            {
                return null;
            }
        }

        [Authorize]
        public async Task<CommandResponse> DeleteMessages(string ownerName, IdentityType ownerType, MessageTypeFlag type, int? id = null)
        {
            DemandAuthentication();

            //verify if this is a sub request
            if (ownerType == IdentityType.Subverse)
            {
                if (!ModeratorPermission.HasPermission(User.Identity.Name, ownerName, ModeratorAction.DeleteMail))
                {
                    return CommandResponse.FromStatus(Status.Denied, "User does not have rights to modify mail");
                }
            }

            //We are going to use this query as the base to form a protective where clause
            var q = GetMessageQueryDapperBase(ownerName, ownerType, type, MessageState.All);
            var d = new DapperDelete();

            //Set the where and parameters from the base query
            d.Where = q.Where;
            d.Parameters = q.Parameters;

            d.Delete = "m FROM [Message] m";

            if (id.HasValue)
            {
                d.Append(x => x.Where, "m.ID = @ID");
                d.Parameters.Add("ID", id.Value);
            }
           
            var result = await _db.Database.Connection.ExecuteAsync(d.ToString(), d.Parameters);

            //if (id.HasValue)
            //{
            //    var q = GetMessageQueryBase(ownerName, ownerType, type, MessageState.All);
            //    q = q.Where(x => x.ID == id.Value);
            //    var message = q.FirstOrDefault();

            //    if (message != null)
            //    {
            //        _db.Messages.Remove(message);
            //        await _db.SaveChangesAsync().ConfigureAwait(false);
            //    }
            //}
            //else
            //{
            //    using (var db = new voatEntities())
            //    {
            //        var q = GetMessageQueryBase(db, ownerName, ownerType, type, MessageState.All);
            //        await q.ForEachAsync(x => db.Messages.Remove(x)).ConfigureAwait(false);
            //        await db.SaveChangesAsync().ConfigureAwait(false);
            //    }
            //}

            Task.Run(() => EventNotification.Instance.SendMessageNotice(
                       UserDefinition.Format(ownerName, ownerType),
                       UserDefinition.Format(ownerName, ownerType),
                       type,
                       null,
                       null));

            return CommandResponse.FromStatus(Status.Success, "");
        }

        [Authorize]
        public async Task<CommandResponse> MarkMessages(string ownerName, IdentityType ownerType, MessageTypeFlag type, MessageState state, int? id = null)
        {
            DemandAuthentication();

            //verify if this is a sub request
            if (ownerType == IdentityType.Subverse)
            {
                if (!ModeratorPermission.HasPermission(User.Identity.Name, ownerName, ModeratorAction.ReadMail))
                {
                    return CommandResponse.FromStatus(Status.Denied, "User does not have rights to modify mail");
                }
            }

            if (state == MessageState.All)
            {
                return CommandResponse.FromStatus(Status.Ignored, "MessageState must be either Read or Unread");
            }

            var stateToFind = (state == MessageState.Read ? MessageState.Unread : MessageState.Read);
            var setReadDate = new Func<Data.Models.Message, DateTime?>((x) => (state == MessageState.Read ? CurrentDate : (DateTime?)null));

            //We are going to use this query as the base to form a protective where clause
            var q = GetMessageQueryDapperBase(ownerName, ownerType, type, stateToFind);
            var u = new DapperUpdate();

            //Set the where and parameters from the base query
            u.Where = q.Where;
            u.Parameters = q.Parameters;

            u.Update = "m SET m.ReadDate = @ReadDate FROM [Message] m";

            if (id.HasValue)
            {
                u.Append(x => x.Where, "m.ID = @ID");
                u.Parameters.Add("ID", id.Value);
            }

            u.Parameters.Add("ReadDate", CurrentDate);

            var result = await _db.Database.Connection.ExecuteAsync(u.ToString(), u.Parameters);

            Task.Run(() => EventNotification.Instance.SendMessageNotice(
                        UserDefinition.Format(ownerName, ownerType),
                        UserDefinition.Format(ownerName, ownerType),
                        type,
                        null,
                        null));

            return CommandResponse.FromStatus(Status.Success, "");
        }

        [Authorize]
        public async Task<MessageCounts> GetMessageCounts(string ownerName, IdentityType ownerType, MessageTypeFlag type, MessageState state)
        {
            #region Dapper

            using (var db = new voatEntities())
            {
                var q = new DapperQuery();
                q.Select = "SELECT [Type], Count = COUNT(*) FROM [Message] WITH (NOLOCK)";
                q.Where =
                    @"(
                        (Recipient = @UserName AND RecipientType = @OwnerType AND [Type] <> @SentType)
                        OR
                        (Sender = @UserName AND SenderType = @OwnerType AND [Type] = @SentType)
                    )";

                //Filter
                if (state != MessageState.All)
                {
                    q.Where += String.Format(" AND ReadDate IS {0} NULL", state == MessageState.Unread ? "" : "NOT");
                }

                var types = ConvertMessageTypeFlag(type);
                if (types != null)
                {
                    q.Where += String.Format(" AND [Type] IN @MessageTypes");
                }

                q.GroupBy = "[Type]";
                q.Parameters = new {
                    UserName = ownerName,
                    OwnerType = (int)ownerType,
                    SentType = (int)MessageType.Sent,
                    MessageTypes = (types != null ? types.ToArray() : (int[])null)
                }.ToDynamicParameters();

                var results = await db.Database.Connection.QueryAsync<MessageCount>(q.ToString(), q.Parameters);

                var result = new MessageCounts(UserDefinition.Create(ownerName, ownerType));
                result.Counts = results.ToList();
                return result;
            }

            #endregion

            #region EF
            //var q = GetMessageQueryBase(ownerName, ownerType, type, state);
            //var counts = await q.GroupBy(x => x.Type).Select(x => new { x.Key, Count = x.Count() }).ToListAsync();

            //var result = new MessageCounts(UserDefinition.Create(ownerName, ownerType));
            //foreach (var count in counts)
            //{
            //    result.Counts.Add(new MessageCount() { Type = (MessageType)count.Key, Count = count.Count });
            //}
            //return result;
            #endregion

           
        }

        [Authorize]
        public async Task<IEnumerable<Domain.Models.Message>> GetMessages(MessageTypeFlag type, MessageState state, bool markAsRead = true, SearchOptions options = null)
        {
            return await GetMessages(User.Identity.Name, IdentityType.User, type, state, markAsRead, options).ConfigureAwait(false);
        }

        [Authorize]
        public async Task<IEnumerable<Domain.Models.Message>> GetMessages(string ownerName, IdentityType ownerType, MessageTypeFlag type, MessageState state, bool markAsRead = true, SearchOptions options = null)
        {
            DemandAuthentication();
            if (options == null)
            {
                options = SearchOptions.Default;
            }
            using (var db = new voatEntities())
            {
                var q = GetMessageQueryDapperBase(ownerName, ownerType, type, state);
                q.SkipCount = options.Index;
                q.TakeCount = options.Count;

                var messageMap = new Func<Data.Models.Message, Data.Models.Submission, Data.Models.Comment, Domain.Models.Message>((m, s, c) => {
                    var msg = m.Map();
                    msg.Submission = s.Map();
                    msg.Comment = c.Map(m.Subverse);


                    //Set Message Title/Info for API Output
                    switch (msg.Type)
                    {
                        case MessageType.CommentMention:
                        case MessageType.CommentReply:
                        case MessageType.SubmissionReply:
                            if (msg.Comment != null)
                            {
                                msg.Title = msg.Submission?.Title;
                                msg.Content = msg.Comment?.Content;
                                msg.FormattedContent = msg.Comment?.FormattedContent;
                            }
                            else
                            {
                                msg.Title = "Removed";
                                msg.Content = "Removed";
                                msg.FormattedContent = "Removed";
                            }
                            break;
                        case MessageType.SubmissionMention:
                            if (msg.Submission != null)
                            {
                                msg.Title = msg.Submission?.Title;
                                msg.Content = msg.Submission?.Content;
                                msg.FormattedContent = msg.Submission?.FormattedContent;
                            }
                            else
                            {
                                msg.Title = "Removed";
                                msg.Content = "Removed";
                                msg.FormattedContent = "Removed";
                            }
                            break;
                    }

                    return msg;
                });

                var messages = await _db.Database.Connection.QueryAsync<Data.Models.Message, Data.Models.Submission, Data.Models.Comment, Domain.Models.Message>(q.ToString(), messageMap, q.Parameters, splitOn: "ID");

                //mark as read
                if (markAsRead && messages.Any(x => x.ReadDate == null))
                {
                    var update = new DapperUpdate();
                    update.Update = "m SET m.ReadDate = @CurrentDate FROM [Message] m";
                    update.Where = "m.ReadDate IS NULL AND m.ID IN @IDs";
                    update.Parameters.Add("CurrentDate", Repository.CurrentDate);
                    update.Parameters.Add("IDs", messages.Where(x => x.ReadDate == null).Select(x => x.ID).ToArray());

                    await _db.Database.Connection.ExecuteAsync(update.ToString(), update.Parameters);

                    //await q.Where(x => x.ReadDate == null).ForEachAsync<Models.Message>(x => x.ReadDate = CurrentDate).ConfigureAwait(false);
                    //await db.SaveChangesAsync().ConfigureAwait(false);

                    Task.Run(() => EventNotification.Instance.SendMessageNotice(
                       UserDefinition.Format(ownerName, ownerType),
                       UserDefinition.Format(ownerName, ownerType),
                       type,
                       null,
                       null));

                }
                return messages;
            }
        }

        #endregion UserMessages

        #region User Related Functions

        public IEnumerable<VoteValue> UserCommentVotesBySubmission(string userName, int submissionID)
        {
            IEnumerable<VoteValue> result = null;
            var q = new DapperQuery();

            q.Select = "SELECT [ID] = v.CommentID, [Value] = IsNull(v.VoteStatus, 0) FROM CommentVoteTracker v WITH (NOLOCK) INNER JOIN Comment c WITH (NOLOCK) ON CommentID = c.ID";
            q.Where = "v.UserName = @UserName AND c.SubmissionID = @ID";

            result = _db.Database.Connection.Query<VoteValue>(q.ToString(), new { UserName = userName, ID = submissionID });

            return result;

            //List<CommentVoteTracker> vCache = new List<CommentVoteTracker>();

            //if (!String.IsNullOrEmpty(userName))
            //{
            //    vCache = (from cv in _db.CommentVoteTrackers.AsNoTracking()
            //              join c in _db.Comments on cv.CommentID equals c.ID
            //              where c.SubmissionID == submissionID && cv.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
            //              select cv).ToList();
            //}
            //return vCache;
        }
        [Obsolete("Arg Matie, you shipwrecked upon t'is Dead Code", true)]
        public IEnumerable<CommentSaveTracker> UserCommentSavedBySubmission(int submissionID, string userName)
        {
            List<CommentSaveTracker> vCache = new List<CommentSaveTracker>();

            if (!String.IsNullOrEmpty(userName))
            {
                vCache = (from cv in _db.CommentSaveTrackers.AsNoTracking()
                          join c in _db.Comments on cv.CommentID equals c.ID
                          where c.SubmissionID == submissionID && cv.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
                          select cv).ToList();
            }
            return vCache;
        }

        public IEnumerable<DomainReference> GetSubscriptions(string userName)
        {

            var d = new DapperQuery();
            d.Select = @"SELECT Type = 1, s.Name, OwnerName = NULL FROM SubverseSet subSet
                        INNER JOIN SubverseSetList setList ON subSet.ID = setList.SubverseSetID
                        INNER JOIN Subverse s ON setList.SubverseID = s.ID
                        WHERE subSet.Type = @Type AND subSet.Name = @SetName AND subSet.UserName = @UserName
                        UNION ALL
                        SELECT Type = 2, subSet.Name, OwnerName = subSet.UserName FROM SubverseSetSubscription setSub
                        INNER JOIN SubverseSet subSet ON subSet.ID = setSub.SubverseSetID
                        WHERE setSub.UserName = @UserName";
            d.Parameters = new DynamicParameters(new { UserName = userName, Type = (int)SetType.Front, SetName = SetType.Front.ToString() });

            var results = _db.Database.Connection.Query<DomainReference>(d.ToString(), d.Parameters);

            ////TODO: Set Change - needs to retrun subs in user Front set instead.
            //var subs = (from x in _db.SubverseSets
            //            join y in _db.SubverseSetLists on x.ID equals y.SubverseSetID
            //            join s in _db.Subverses on y.SubverseID equals s.ID
            //            where x.Name == SetType.Front.ToString() && x.UserName == userName && x.Type == (int)SetType.Front
            //            select new DomainReference() { Name = s.Name, Type = DomainType.Subverse }
            //            ).ToList();


            ////var subs = (from x in _db.SubverseSubscriptions
            ////            where x.UserName == userName
            ////            select new DomainReference() { Name = x.Subverse, Type = DomainType.Subverse }
            ////            ).ToList();

            ////var sets = (from x in _db.UserSetSubscriptions
            ////            where x.UserName == userName
            ////            select new DomainReference() { Name = x.UserSet.Name, Type = DomainType.Set }).ToList();

            ////subs.AddRange(sets);


            return results;
        }

        public IList<BlockedItem> GetBlockedUsers(string userName)
        {
            var blocked = (from x in _db.UserBlockedUsers
                           where x.UserName == userName
                           select new BlockedItem() { Name = x.BlockUser, Type = DomainType.User, CreationDate = x.CreationDate }).ToList();
            return blocked;
        }

        //SET: Backwards Compat
        public async Task<IEnumerable<BlockedItem>> GetBlockedSubverses(string userName)
        {

            var setList = await GetSetListDescription(SetType.Blocked.ToString(), userName);
            var blocked = setList.Select(x => new BlockedItem()
            {
                Name = x.Name,
                Type = DomainType.Subverse,
                CreationDate = x.CreationDate
            }).ToList();

            //var blocked = (from x in _db.UserBlockedSubverses
            //               where x.UserName == userName
            //               select new BlockedItem() {
            //                   Name = x.Subverse,
            //                   Type = DomainType.Subverse,
            //                   CreationDate = x.CreationDate
            //               }).ToList();
            return blocked;
        }

        public async Task<UserInformation> GetUserInformation(string userName)
        {
            if (String.IsNullOrWhiteSpace(userName) || userName.TrimSafe().IsEqual("deleted"))
            {
                return null;
            }
            //THIS COULD BE A SOURCE OF BLOCKING
            var q = new QueryUserRecord(userName, CachePolicy.None); //Turn off cache retrieval for this
            var userRecord = await q.ExecuteAsync().ConfigureAwait(false);

            if (userRecord == null)
            {
                //not a valid user
                //throw new VoatNotFoundException("Can not find user record for " + userName);
                return null;
            }

            userName = userRecord.UserName;

            var userInfo = new UserInformation();
            userInfo.UserName = userRecord.UserName;
            userInfo.RegistrationDate = userRecord.RegistrationDateTime;

            Task<Score>[] tasks = { Task<Score>.Factory.StartNew(() => UserContributionPoints(userName, ContentType.Comment, null, true)),
                                    Task<Score>.Factory.StartNew(() => UserContributionPoints(userName, ContentType.Submission, null, true)),
                                    Task<Score>.Factory.StartNew(() => UserContributionPoints(userName, ContentType.Submission, null, false)),
                                    Task<Score>.Factory.StartNew(() => UserContributionPoints(userName, ContentType.Comment, null, false)),
            };

            var userPreferences = await GetUserPreferences(userName).ConfigureAwait(false);
            
            //var pq = new QueryUserPreferences(userName);
            //var userPreferences = await pq.ExecuteAsync();
            //var userPreferences = await GetUserPreferences(userName);

            userInfo.Bio = String.IsNullOrWhiteSpace(userPreferences.Bio) ? STRINGS.DEFAULT_BIO : userPreferences.Bio;
            userInfo.ProfilePicture = VoatPathHelper.AvatarPath(userName, userPreferences.Avatar, true, true, !String.IsNullOrEmpty(userPreferences.Avatar));

            //Task.WaitAll(tasks);
            await Task.WhenAll(tasks).ConfigureAwait(false);

            userInfo.CommentPoints = tasks[0].Result;
            userInfo.SubmissionPoints = tasks[1].Result;
            userInfo.SubmissionVoting = tasks[2].Result;
            userInfo.CommentVoting = tasks[3].Result;

            //Old Sequential
            //userInfo.CommentPoints = UserContributionPoints(userName, ContentType.Comment);
            //userInfo.SubmissionPoints = UserContributionPoints(userName, ContentType.Submission);
            //userInfo.SubmissionVoting = UserVotingBehavior(userName, ContentType.Submission);
            //userInfo.CommentVoting = UserVotingBehavior(userName, ContentType.Comment);

            //Badges
            var userBadges = await (from b in _db.Badges
                              join ub in _db.UserBadges on b.ID equals ub.BadgeID into ubn
                              from uball in ubn.DefaultIfEmpty()
                              where
                              uball.UserName == userName

                              //(virtual badges)
                              ||
                              (b.ID == "whoaverse" && (userInfo.RegistrationDate < new DateTime(2015, 1, 2)))
                              ||
                              (b.ID == "alphauser" && (userInfo.RegistrationDate > new DateTime(2015, 1, 2) && userInfo.RegistrationDate < new DateTime(2016, 10, 10)))
                              ||
                              (b.ID == "betauser" && userInfo.RegistrationDate > (new DateTime(2016, 10, 10)))
                              ||
                              (b.ID == "cakeday" && userInfo.RegistrationDate.Year < CurrentDate.Year && userInfo.RegistrationDate.Month == CurrentDate.Month && userInfo.RegistrationDate.Day == CurrentDate.Day)
                              select new Voat.Domain.Models.UserBadge()
                              {
                                  CreationDate = (uball == null ? userInfo.RegistrationDate : uball.CreationDate),
                                  Name = b.Name,
                                  Title = b.Title,
                                  Graphic = b.Graphic,
                              }
                              ).ToListAsync().ConfigureAwait(false);

            userInfo.Badges = userBadges;

            return userInfo;
        }

        public async Task<Models.UserPreference> GetUserPreferences(string userName)
        {
            Models.UserPreference result = null;
            if (!String.IsNullOrEmpty(userName))
            {
                var query = _db.UserPreferences.Where(x => (x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)));

                result = await query.FirstOrDefaultAsync().ConfigureAwait(false);
            }

            if (result == null)
            {
                result = new Data.Models.UserPreference();
                Repository.SetDefaultUserPreferences(result);
                result.UserName = userName;
            }

            return result;
        }

        public Score UserVotingBehavior(string userName, ContentType contentType = ContentType.Comment | ContentType.Submission, TimeSpan? timeSpan = null)
        {
            Score vb = UserContributionPoints(userName, contentType, null, false, timeSpan);

            //if ((type & ContentType.Comment) > 0)
            //{
            //    var c = GetUserVotingBehavior(userName, ContentType.Comment, span);
            //    vb.Combine(c);
            //}
            //if ((type & ContentType.Submission) > 0)
            //{
            //    var c = GetUserVotingBehavior(userName, ContentType.Submission, span);
            //    vb.Combine(c);
            //}

            return vb;
        }
        [Obsolete("User UserContributionPoints instead when implemented", true)]
        private Score GetUserVotingBehavior(string userName, ContentType type, TimeSpan? span = null)
        {
            var score = new Score();
            using (var db = new voatEntities())
            {
                DateTime? compareDate = null;
                if (span.HasValue)
                {
                    compareDate = CurrentDate.Subtract(span.Value);
                }

                var cmd = db.Database.Connection.CreateCommand();
                cmd.CommandText = String.Format(
                                    @"SELECT x.VoteStatus, 'Count' = ABS(ISNULL(SUM(x.VoteStatus), 0))
                                FROM {0} x WITH (NOLOCK)
                                WHERE x.UserName = @UserName
                                AND (x.CreationDate >= @CompareDate OR @CompareDate IS NULL)
                                GROUP BY x.VoteStatus", type == ContentType.Comment ? "CommentVoteTracker" : "SubmissionVoteTracker");
                cmd.CommandType = System.Data.CommandType.Text;

                var param = cmd.CreateParameter();
                param.ParameterName = "UserName";
                param.DbType = System.Data.DbType.String;
                param.Value = userName;
                cmd.Parameters.Add(param);

                param = cmd.CreateParameter();
                param.ParameterName = "CompareDate";
                param.DbType = System.Data.DbType.DateTime;
                param.Value = compareDate.HasValue ? compareDate.Value : (object)DBNull.Value;
                cmd.Parameters.Add(param);

                if (cmd.Connection.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }
                using (var reader = cmd.ExecuteReader(System.Data.CommandBehavior.CloseConnection))
                {
                    while (reader.Read())
                    {
                        int voteStatus = (int)reader["VoteStatus"];
                        if (voteStatus == 1)
                        {
                            score.UpCount = (int)reader["Count"];
                        }
                        else if (voteStatus == -1)
                        {
                            score.DownCount = (int)reader["Count"];
                        }
                    }
                }
            }
            return score;
        }
        public int UserVoteStatus(string userName, ContentType type, int id)
        {
            var result = UserVoteStatus(userName, type, new int[] { id });
            if (result.Any())
            {
                return result.First().Value;
            }
            return 0;
        }
        public IEnumerable<VoteValue> UserVoteStatus(string userName, ContentType type, int[] id)
        {
            IEnumerable<VoteValue> result = null;
            var q = new DapperQuery();

            switch (type)
            {
                case ContentType.Comment:
                    q.Select = "SELECT [ID] = CommentID, [Value] = IsNull(VoteStatus, 0) FROM CommentVoteTracker WITH (NOLOCK)";
                    q.Where = "UserName = @UserName AND CommentID IN @ID";
                    break;
                case ContentType.Submission:
                    q.Select = "SELECT [ID] = SubmissionID, [Value] = IsNull(VoteStatus, 0) FROM SubmissionVoteTracker WITH (NOLOCK)";
                    q.Where = "UserName = @UserName AND SubmissionID IN @ID";
                    break;
            }

            result = _db.Database.Connection.Query<VoteValue>(q.ToString(), new { UserName = userName, ID = id });

            return result;
        }
        public int UserCommentCount(string userName, TimeSpan? span, string subverse = null)
        {
            DateTime? compareDate = null;
            if (span.HasValue)
            {
                compareDate = CurrentDate.Subtract(span.Value);
            }

            var result = (from x in _db.Comments
                          where
                            x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
                            && (x.Submission.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase) || subverse == null)
                            && (compareDate.HasValue && x.CreationDate >= compareDate)
                          select x).Count();
            return result;
        }

        public int UserSubmissionCount(string userName, TimeSpan? span, SubmissionType? type = null, string subverse = null)
        {
            DateTime? compareDate = null;
            if (span.HasValue)
            {
                compareDate = CurrentDate.Subtract(span.Value);
            }
            var q = new DapperQuery();
            q.Select = "COUNT(*) FROM Submission WITH (NOLOCK)";
            q.Where = "UserName = @UserName";
            if (compareDate != null)
            {
                q.Append(x => x.Where, "CreationDate >= @StartDate");
            }
            if (type != null)
            {
                q.Append(x => x.Where, "Type = @Type");
            }
            if (!String.IsNullOrEmpty(subverse))
            {
                q.Append(x => x.Where, "Subverse = @Subverse");
            }

            var count = _db.Database.Connection.ExecuteScalar<int>(q.ToString(), new { UserName = userName, StartDate = compareDate, Type = type, Subverse = subverse });

            //Logic was buggy here
            //var result = (from x in _db.Submissions
            //              where
            //                x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)
            //                &&
            //                ((x.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase) || subverse == null)
            //                && (compareDate.HasValue && x.CreationDate >= compareDate)
            //                && (type != null && x.Type == (int)type.Value) || type == null)
            //              select x).Count();
            //return result;

            return count;
        }
        public Score UserContributionPoints(string userName, ContentType contentType, string subverse = null, bool isReceived = true, TimeSpan? timeSpan = null)
        {

            Func<IEnumerable<dynamic>, Score> processRecords = new Func<IEnumerable<dynamic>, Score>(records => {
                Score score = new Score();
                if (records != null && records.Any())
                {
                    foreach (var record in records)
                    {
                        if (record.VoteStatus == 1)
                        {
                            score.UpCount = isReceived ? (int)record.VoteValue : (int)record.VoteCount;
                        }
                        else if (record.VoteStatus == -1)
                        {
                            score.DownCount = isReceived ? (int)record.VoteValue : (int)record.VoteCount;
                        }
                    }
                }
                return score;
            });

            var groupingClause = @"SELECT UserName, IsReceived, ContentType, VoteStatus, VoteCount = SUM(VoteCount), VoteValue = SUM(VoteValue) 
                        FROM (	
	                        {0}
                        ) AS a
                        GROUP BY a.UserName, a.IsReceived, a.ContentType, a.VoteStatus";
            var archivedPointsClause = @"SELECT UserName, IsReceived, ContentType, VoteStatus, VoteCount, VoteValue
	                        FROM UserContribution AS uc WITH (NOLOCK)
	                        WHERE uc.UserName = @UserName AND uc.IsReceived = @IsReceived AND uc.ContentType = @ContentType
	                        UNION ALL
                    ";
            var alias = "";
            DateTime? dateRange = timeSpan.HasValue ? CurrentDate.Subtract(timeSpan.Value) : (DateTime?)null;
            Score s = new Score();
            using (var db = new voatEntities())
            {
                var contentTypes = contentType.GetEnumFlags();
                foreach (var contentTypeToQuery in contentTypes)
                {
                    var q = new DapperQuery();

                    switch (contentTypeToQuery)
                    {
                        case ContentType.Comment:

                            //basic point calc query
                            q.Select = $@"SELECT UserName = @UserName, IsReceived = @IsReceived, ContentType = @ContentType, VoteStatus = v.VoteStatus, VoteCount = 1, VoteValue = ABS(v.VoteValue)
	                                FROM CommentVoteTracker v WITH (NOLOCK) 
	                                INNER JOIN Comment c WITH (NOLOCK) ON c.ID = v.CommentID
	                                INNER JOIN Submission s ON s.ID = c.SubmissionID";

                            //This controls whether we search for given or received votes
                            alias = (isReceived ? "c" : "v");
                            q.Append(x => x.Where, $"{alias}.UserName = @UserName");

                            break;
                        case ContentType.Submission:
                            //basic point calc query
                            q.Select = $@"SELECT UserName = @UserName, IsReceived = @IsReceived, ContentType = @ContentType, VoteStatus = v.VoteStatus, VoteCount = 1, VoteValue = ABS(v.VoteValue)
	                        FROM SubmissionVoteTracker v WITH (NOLOCK) 
	                        INNER JOIN Submission s ON s.ID = v.SubmissionID";

                            //This controls whether we search for given or received votes
                            alias = (isReceived ? "s" : "v");
                            q.Append(x => x.Where, $"{alias}.UserName = @UserName");

                            break;
                        default:
                            throw new NotImplementedException($"Type {contentType.ToString()} is not supported");
                    }

                    //if subverse/daterange calc we do not use archived table
                    if (!String.IsNullOrEmpty(subverse) || dateRange.HasValue)
                    {
                        if (!String.IsNullOrEmpty(subverse))
                        {
                            q.Append(x => x.Where, "s.Subverse = @Subverse");
                        }
                        if (dateRange.HasValue)
                        {
                            q.Append(x => x.Where, "v.CreationDate >= @DateRange");
                        }
                    }
                    else
                    {
                        q.Select = archivedPointsClause + q.Select;
                        q.Append(x => x.Where, "s.ArchiveDate IS NULL");
                    }

                    string statement = String.Format(groupingClause, q.ToString());
                    System.Diagnostics.Debug.Print("Query Output");
                    System.Diagnostics.Debug.Print(statement);
                    var records = db.Database.Connection.Query(statement, new
                    {
                        UserName = userName,
                        IsReceived = isReceived,
                        Subverse = subverse,
                        ContentType = (int)contentType,
                        DateRange = dateRange
                    });
                    Score result = processRecords(records);
                    s.Combine(result);
                }
            }
            return s;
        }

        //public Score UserContributionPoints_OLD(string userName, ContentType type, string subverse = null)
        //{
        //    Score s = new Score();
        //    using (var db = new voatEntities())
        //    {
        //        if ((type & ContentType.Comment) > 0)
        //        {
        //            var cmd = db.Database.Connection.CreateCommand();
        //            cmd.CommandText = @"SELECT 'UpCount' = CAST(ABS(ISNULL(SUM(c.UpCount),0)) AS INT), 'DownCount' = CAST(ABS(ISNULL(SUM(c.DownCount),0)) AS INT) FROM Comment c WITH (NOLOCK)
        //                            INNER JOIN Submission s WITH (NOLOCK) ON(c.SubmissionID = s.ID)
        //                            WHERE c.UserName = @UserName
        //                            AND (s.Subverse = @Subverse OR @Subverse IS NULL)
        //                            AND c.IsAnonymized = 0"; //this prevents anon votes from showing up in stats
        //            cmd.CommandType = System.Data.CommandType.Text;

        //            var param = cmd.CreateParameter();
        //            param.ParameterName = "UserName";
        //            param.DbType = System.Data.DbType.String;
        //            param.Value = userName;
        //            cmd.Parameters.Add(param);

        //            param = cmd.CreateParameter();
        //            param.ParameterName = "Subverse";
        //            param.DbType = System.Data.DbType.String;
        //            param.Value = String.IsNullOrEmpty(subverse) ? (object)DBNull.Value : subverse;
        //            cmd.Parameters.Add(param);

        //            if (cmd.Connection.State != System.Data.ConnectionState.Open)
        //            {
        //                cmd.Connection.Open();
        //            }
        //            using (var reader = cmd.ExecuteReader(System.Data.CommandBehavior.CloseConnection))
        //            {
        //                if (reader.Read())
        //                {
        //                    s.Combine(new Score() { UpCount = (int)reader["UpCount"], DownCount = (int)reader["DownCount"] });
        //                }
        //            }
        //        }

        //        if ((type & ContentType.Submission) > 0)
        //        {
        //            var cmd = db.Database.Connection.CreateCommand();
        //            cmd.CommandText = @"SELECT 
        //                            'UpCount' = CAST(ABS(ISNULL(SUM(s.UpCount), 0)) AS INT), 
        //                            'DownCount' = CAST(ABS(ISNULL(SUM(s.DownCount), 0)) AS INT) 
        //                            FROM Submission s WITH (NOLOCK)
        //                            WHERE s.UserName = @UserName
        //                            AND (s.Subverse = @Subverse OR @Subverse IS NULL)
        //                            AND s.IsAnonymized = 0";

        //            var param = cmd.CreateParameter();
        //            param.ParameterName = "UserName";
        //            param.DbType = System.Data.DbType.String;
        //            param.Value = userName;
        //            cmd.Parameters.Add(param);

        //            param = cmd.CreateParameter();
        //            param.ParameterName = "Subverse";
        //            param.DbType = System.Data.DbType.String;
        //            param.Value = String.IsNullOrEmpty(subverse) ? (object)DBNull.Value : subverse;
        //            cmd.Parameters.Add(param);

        //            if (cmd.Connection.State != System.Data.ConnectionState.Open)
        //            {
        //                cmd.Connection.Open();
        //            }
        //            using (var reader = cmd.ExecuteReader(System.Data.CommandBehavior.CloseConnection))
        //            {
        //                if (reader.Read())
        //                {
        //                    s.Combine(new Score() { UpCount = (int)reader["UpCount"], DownCount = (int)reader["DownCount"] });
        //                }
        //            }
        //        }
        //    }
        //    return s;
        //}

        [Authorize]
        public async Task<CommandResponse<bool?>> SubscribeUser(DomainReference domainReference, SubscriptionAction action)
        {
            DemandAuthentication();

            CommandResponse<bool?> response = new CommandResponse<bool?>(null, Status.NotProcessed, "");

            switch (domainReference.Type)
            {
                case DomainType.Subverse:
                    
                    var subverse = GetSubverseInfo(domainReference.Name);
                    if (subverse == null)
                    {
                        return CommandResponse.FromStatus<bool?>(null, Status.Denied, "Subverse does not exist");
                    }
                    if (subverse.IsAdminDisabled.HasValue && subverse.IsAdminDisabled.Value)
                    {
                        return CommandResponse.FromStatus<bool?>(null, Status.Denied, "Subverse is disabled");
                    }

                    var set = GetOrCreateSubverseSet(new SubverseSet() { Name = SetType.Front.ToString(), UserName = User.Identity.Name, Type = (int)SetType.Front, Description = "Front Page Subverse Subscriptions" });

                    response = await SetSubverseListChange(set, subverse, action);


                    //var countChanged = false;

                    //if (action == SubscriptionAction.Subscribe)
                    //{
                    //    if (!_db.SubverseSubscriptions.Any(x => x.Subverse.Equals(domainReference.Name, StringComparison.OrdinalIgnoreCase) && x.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase)))
                    //    {
                    //        var sub = new SubverseSubscription { UserName = User.Identity.Name, Subverse = domainReference.Name };
                    //        _db.SubverseSubscriptions.Add(sub);
                    //        countChanged = true;
                    //    }
                    //}
                    //else
                    //{
                    //    var sub = _db.SubverseSubscriptions.FirstOrDefault(x => x.Subverse.Equals(domainReference.Name, StringComparison.OrdinalIgnoreCase) && x.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase));
                    //    if (sub != null)
                    //    {
                    //        _db.SubverseSubscriptions.Remove(sub);
                    //        countChanged = true;
                    //    }
                    //}

                    //await _db.SaveChangesAsync().ConfigureAwait(false);
                    //if (countChanged)
                    //{
                    //    await UpdateSubverseSubscriberCount(domainReference, action).ConfigureAwait(false);
                    //}

                    break;
                case DomainType.Set:
                    var setb = GetSet(domainReference.Name, domainReference.OwnerName);
                    if (setb == null)
                    {
                        return CommandResponse.FromStatus<bool?>(null,Status.Denied, "Set does not exist");
                    }

                    var subscribeAction = SubscriptionAction.Toggle;

                    var setSubscriptionRecord = _db.SubverseSetSubscriptions.FirstOrDefault(x => x.SubverseSetID == setb.ID && x.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase));

                    if (setSubscriptionRecord == null && ((action == SubscriptionAction.Subscribe) || action == SubscriptionAction.Toggle))
                    {
                        var sub = new SubverseSetSubscription { UserName = User.Identity.Name, SubverseSetID = setb.ID, CreationDate = CurrentDate };
                        _db.SubverseSetSubscriptions.Add(sub);
                        subscribeAction = SubscriptionAction.Subscribe;
                        response.Response = true;


                        //db.SubverseSetLists.Add(new SubverseSetList { SubverseSetID = set.ID, SubverseID = subverse.ID, CreationDate = CurrentDate });
                        //response.Response = true;
                    }
                    else if (setSubscriptionRecord != null && ((action == SubscriptionAction.Unsubscribe) || action == SubscriptionAction.Toggle))
                    {
                        _db.SubverseSetSubscriptions.Remove(setSubscriptionRecord);
                        subscribeAction = SubscriptionAction.Unsubscribe;
                        response.Response = false;

                        //db.SubverseSetLists.Remove(setSubverseRecord);
                        //response.Response = false;
                    }



                    //if (action == SubscriptionAction.Subscribe)
                    //{
                    //    if (!_db.SubverseSetSubscriptions.Any(x => x.SubverseSetID == setb.ID && x.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase)))
                    //    {
                    //        var sub = new SubverseSetSubscription { UserName = User.Identity.Name, SubverseSetID = setb.ID };
                    //        _db.SubverseSetSubscriptions.Add(sub);
                    //        countChanged = true;
                    //        response.Response = true;
                    //    }
                    //}
                    //else
                    //{
                    //    var sub = _db.SubverseSetSubscriptions.FirstOrDefault(x => x.SubverseSetID == setb.ID && x.UserName.Equals(User.Identity.Name, StringComparison.OrdinalIgnoreCase));
                    //    if (sub != null)
                    //    {
                    //        _db.SubverseSetSubscriptions.Remove(sub);
                    //        countChanged = true;
                    //        response.Response = false;
                    //    }
                    //}

                    await _db.SaveChangesAsync().ConfigureAwait(false);
                    if (subscribeAction != SubscriptionAction.Toggle)
                    {
                        await UpdateSubverseSubscriberCount(domainReference, subscribeAction).ConfigureAwait(false);
                    }
                    response.Status = Status.Success;

                    break;
                default:
                    throw new NotImplementedException(String.Format("{0} subscriptions not implemented yet", domainReference.Type));
                    break;
            }
            return response;
        }

        private async Task UpdateSubverseSubscriberCount(DomainReference domainReference, SubscriptionAction action)
        {
            //TODO: This logic is jacked because of the action has been extended to include a toggle value thus this needs refactoring
            if (action != SubscriptionAction.Toggle)
            {

                int incrementValue = action == SubscriptionAction.Subscribe ? 1 : -1;
                var u = new DapperUpdate();

                switch (domainReference.Type)
                {
                    case DomainType.Subverse:

                        u.Update = "UPDATE s SET SubscriberCount = (SubscriberCount + @IncrementValue) FROM Subverse s";
                        u.Where = "s.Name = @Name";
                        u.Parameters = new DynamicParameters(new { Name = domainReference.Name, IncrementValue = incrementValue });

                        break;
                    case DomainType.Set:
                        u.Update = "UPDATE s SET SubscriberCount = (SubscriberCount + @IncrementValue) FROM SubverseSet s";
                        u.Where = "s.Name = @Name";

                        if (!String.IsNullOrEmpty(domainReference.OwnerName))
                        {
                            u.Append(x => x.Where, "s.UserName = @OwnerName");
                        }
                        else
                        {
                            u.Append(x => x.Where, "s.UserName IS NULL");
                        }
                        u.Parameters = new DynamicParameters(new { Name = domainReference.Name, IncrementValue = incrementValue, OwnerName = domainReference.OwnerName });
                        break;
                    case DomainType.User:
                        throw new NotImplementedException("User subscriber count not implemented");
                        break;
                }
                var count = await _db.Database.Connection.ExecuteAsync(u.ToString(), u.Parameters);
            }
        }

        public async Task<CommandResponse<bool?>> BanUserFromSubverse(string userName, string subverse, string reason, bool? force = null)
        {
            bool? status = null;

            //check perms
            if (!ModeratorPermission.HasPermission(User.Identity.Name, subverse, Domain.Models.ModeratorAction.Banning))
            {
                return new CommandResponse<bool?>(status, Status.Denied, "User does not have permission to ban");
            }

            userName = userName.TrimSafe();

            string originalUserName = UserHelper.OriginalUsername(userName);

            // prevent bans if user doesn't exist
            if (String.IsNullOrEmpty(originalUserName))
            {
                return new CommandResponse<bool?>(status, Status.Denied, "User can not be found? Are you at the right site?");
            }

            // get model for selected subverse
            var subverseModel = GetSubverseInfo(subverse);

            if (subverseModel == null)
            {
                return new CommandResponse<bool?>(status, Status.Denied, "Subverse can not be found");
            }

            // check that user is not already banned in given subverse
            var existingBan = _db.SubverseBans.FirstOrDefault(a => a.UserName == originalUserName && a.Subverse == subverseModel.Name);

            if (existingBan != null && (force.HasValue && force.Value))
            {
                return new CommandResponse<bool?>(status, Status.Denied, "User is currently banned. You can't reban.");
            }

            //Force logic:
            //True = ennsure ban
            //False = ensure remove ban
            //Null = toggle ban
            bool? addBan = (force.HasValue ?
                                (force.Value ?
                                    (existingBan == null ? true : (bool?)null) :
                                    (existingBan == null ? (bool?)null : false))
                            : !(existingBan == null));

            if (addBan.HasValue)
            {
                if (addBan.Value)
                {
                    if (String.IsNullOrWhiteSpace(reason))
                    {
                        return new CommandResponse<bool?>(status, Status.Denied, "Banning a user requires a reason to be given");
                    }
                    // prevent bans of the current user
                    if (User.Identity.Name.Equals(userName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CommandResponse<bool?>(status, Status.Denied, "Can not ban yourself or a blackhole appears");
                    }
                    //check if user is mod for "The benning"
                    if (ModeratorPermission.IsModerator(originalUserName, subverseModel.Name))
                    {
                        return new CommandResponse<bool?>(status, Status.Denied, "Moderators of subverse can not be banned. Is this a coup attempt?");
                    }
                    status = true; //added ban
                    var subverseBan = new Data.Models.SubverseBan();
                    subverseBan.UserName = originalUserName;
                    subverseBan.Subverse = subverseModel.Name;
                    subverseBan.CreatedBy = User.Identity.Name;
                    subverseBan.CreationDate = Repository.CurrentDate;
                    subverseBan.Reason = reason;
                    _db.SubverseBans.Add(subverseBan);
                    await _db.SaveChangesAsync().ConfigureAwait(false);
                }
                else
                {
                    status = false; //removed ban
                    _db.SubverseBans.Remove(existingBan);
                    await _db.SaveChangesAsync().ConfigureAwait(false);
                }
            }

            if (status.HasValue)
            {
                var msg = new SendMessage();
                msg.Sender = $"v/{subverseModel.Name}";
                msg.Recipient = originalUserName;

                if (status.Value)
                {
                    //send ban msg
                    msg.Subject = $"You've been banned from v/{subverse} :(";
                    msg.Message = $"@{User.Identity.Name} has banned you from v/{subverseModel.Name} for the following reason: *{reason}*";
                }
                else
                {
                    //send unban msg
                    msg.Subject = $"You've been unbanned from v/{subverse} :)";
                    msg.Message = $"@{User.Identity.Name} has unbanned you from v/{subverseModel.Name}. Play nice. Promise me. Ok, I believe you.";
                }
                SendMessage(msg);
            }
            return new CommandResponse<bool?>(status, Status.Success, "");
        }

        #endregion User Related Functions

        #region ModLog

        public async Task<IEnumerable<Domain.Models.SubverseBan>> GetModLogBannedUsers(string subverse, SearchOptions options)
        {
            using (var db = new voatEntities(CONSTANTS.CONNECTION_READONLY))
            {
                var data = (from b in db.SubverseBans
                            where b.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase)
                            select new Domain.Models.SubverseBan
                            {
                                CreatedBy = b.CreatedBy,
                                CreationDate = b.CreationDate,
                                Reason = b.Reason,
                                Subverse = b.Subverse,
                                ID = b.ID,
                                UserName = b.UserName
                            });
                data = data.OrderByDescending(x => x.CreationDate).Skip(options.Index).Take(options.Count);
                var results = await data.ToListAsync().ConfigureAwait(false);
                return results;
            }
        }
        public async Task<IEnumerable<Data.Models.SubmissionRemovalLog>> GetModLogRemovedSubmissions(string subverse, SearchOptions options)
        {
            using (var db = new voatEntities(CONSTANTS.CONNECTION_READONLY))
            {
                db.EnableCacheableOutput();

                var data = (from b in db.SubmissionRemovalLogs
                            join s in db.Submissions on b.SubmissionID equals s.ID
                            where s.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase)
                            select b).Include(x => x.Submission);

                data = data.OrderByDescending(x => x.CreationDate).Skip(options.Index).Take(options.Count);
                var results = await data.ToListAsync().ConfigureAwait(false);
                return results;
            }
        }
        public async Task<IEnumerable<Domain.Models.CommentRemovalLog>> GetModLogRemovedComments(string subverse, SearchOptions options)
        {
            using (var db = new voatEntities(CONSTANTS.CONNECTION_READONLY))
            {
                db.EnableCacheableOutput();

                var data = (from b in db.CommentRemovalLogs
                            join c in db.Comments on b.CommentID equals c.ID
                            join s in db.Submissions on c.SubmissionID equals s.ID
                            where s.Subverse.Equals(subverse, StringComparison.OrdinalIgnoreCase)
                            select b).Include(x => x.Comment).Include(x => x.Comment.Submission);

                data = data.OrderByDescending(x => x.CreationDate).Skip(options.Index).Take(options.Count);
                var results = await data.ToListAsync().ConfigureAwait(false);

                //TODO: Move to DomainMaps
                var mapToDomain = new Func<Data.Models.CommentRemovalLog, Domain.Models.CommentRemovalLog>(d => 
                {
                    var m = new Domain.Models.CommentRemovalLog();
                    m.CreatedBy = d.Moderator;
                    m.Reason = d.Reason;
                    m.CreationDate = d.CreationDate;

                    m.Comment = new SubmissionComment();
                    m.Comment.ID = d.Comment.ID;
                    m.Comment.UpCount = (int)d.Comment.UpCount;
                    m.Comment.DownCount = (int)d.Comment.DownCount;
                    m.Comment.Content = d.Comment.Content;
                    m.Comment.FormattedContent = d.Comment.FormattedContent;
                    m.Comment.IsDeleted = d.Comment.IsDeleted;
                    m.Comment.CreationDate = d.Comment.CreationDate;

                    m.Comment.IsAnonymized = d.Comment.IsAnonymized;
                    m.Comment.UserName = m.Comment.IsAnonymized ? d.Comment.ID.ToString() : d.Comment.UserName;
                    m.Comment.LastEditDate = d.Comment.LastEditDate;
                    m.Comment.ParentID = d.Comment.ParentID;
                    m.Comment.Subverse = d.Comment.Submission.Subverse;
                    m.Comment.SubmissionID = d.Comment.SubmissionID;

                    m.Comment.Submission.Title = d.Comment.Submission.Title;
                    m.Comment.Submission.IsAnonymized = d.Comment.Submission.IsAnonymized;
                    m.Comment.Submission.UserName = m.Comment.Submission.IsAnonymized ? d.Comment.Submission.ID.ToString() : d.Comment.Submission.UserName;
                    m.Comment.Submission.IsDeleted = d.Comment.Submission.IsDeleted;
                    
                    return m;
                });

                var mapped = results.Select(mapToDomain).ToList();

                return mapped;
            }
        }

        

        #endregion

        #region Moderator Functions

        public async Task<CommandResponse<RemoveModeratorResponse>> RemoveModerator(int subverseModeratorRecordID, bool allowSelfRemovals)
        {
            DemandAuthentication();

            var response = new RemoveModeratorResponse();
            var originUserName = User.Identity.Name;

            // get moderator name for selected subverse
            var subModerator = await _db.SubverseModerators.FindAsync(subverseModeratorRecordID).ConfigureAwait(false);
            if (subModerator == null)
            {
                return new CommandResponse<RemoveModeratorResponse>(response, Status.Invalid, "Can not find record");
            }

            //Set response data
            response.SubverseModerator = subModerator;
            response.OriginUserName = originUserName;
            response.TargetUserName = subModerator.UserName;
            response.Subverse = subModerator.Subverse;

            var subverse = GetSubverseInfo(subModerator.Subverse);
            if (subverse == null)
            {
                return new CommandResponse<RemoveModeratorResponse>(response, Status.Invalid, "Can not find subverse");
            }

            // check if caller has clearance to remove a moderator
            if (!ModeratorPermission.HasPermission(originUserName, subverse.Name, Domain.Models.ModeratorAction.RemoveMods))
            {
                return new CommandResponse<RemoveModeratorResponse>(response, Status.Denied, "User doesn't have permissions to execute action");
            }

            var allowRemoval = false;
            var errorMessage = "Rules do not allow removal";

            if (allowSelfRemovals && originUserName.Equals(subModerator.UserName, StringComparison.OrdinalIgnoreCase))
            {
                allowRemoval = true;
            }
            else if (subModerator.UserName.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                allowRemoval = false;
                errorMessage = "System moderators can not be removed or they get sad";
            }
            else
            {
                //Determine if removal is allowed:
                //Logic:
                //L1: Can remove L1's but only if they invited them / or they were added after them
                var currentModLevel = ModeratorPermission.Level(originUserName, subverse.Name).Value; //safe to get value as previous check ensures is mod
                var targetModLevel = (ModeratorLevel)subModerator.Power;

                switch (currentModLevel)
                {
                    case ModeratorLevel.Owner:
                        if (targetModLevel == ModeratorLevel.Owner)
                        {
                            var isTargetOriginalMod = (String.IsNullOrEmpty(subModerator.CreatedBy) && !subModerator.CreationDate.HasValue); //Currently original mods have these fields nulled
                            if (isTargetOriginalMod)
                            {
                                allowRemoval = false;
                                errorMessage = "The creator can not be destroyed";
                            }
                            else
                            {
                                //find current mods record
                                var originModeratorRecord = _db.SubverseModerators.FirstOrDefault(x =>
                                    x.Subverse.Equals(subModerator.Subverse, StringComparison.OrdinalIgnoreCase)
                                    && x.UserName.Equals(originUserName, StringComparison.OrdinalIgnoreCase));

                                //Creators of subs have no creation date so set it low
                                var originModCreationDate = (originModeratorRecord.CreationDate.HasValue ? originModeratorRecord.CreationDate.Value : new DateTime(2000, 1, 1));

                                if (originModeratorRecord == null)
                                {
                                    allowRemoval = false;
                                    errorMessage = "Can not find current mod record";
                                }
                                else
                                {
                                    allowRemoval = (originModCreationDate < subModerator.CreationDate);
                                    errorMessage = "Moderator has seniority. Oldtimers can't be removed by a young'un";
                                }
                            }
                        }
                        else
                        {
                            allowRemoval = true;
                        }
                        break;

                    default:
                        allowRemoval = (targetModLevel > currentModLevel);
                        errorMessage = "Only moderators at a lower level can be removed";
                        break;
                }
            }

            //ensure mods can only remove mods that are a lower level than themselves
            if (allowRemoval)
            {
                // execute removal
                _db.SubverseModerators.Remove(subModerator);
                await _db.SaveChangesAsync().ConfigureAwait(false);

                ////clear mod cache
                //CacheHandler.Instance.Remove(CachingKey.SubverseModerators(subverse.Name));

                return new CommandResponse<RemoveModeratorResponse>(response, Status.Success, String.Empty);
            }
            else
            {
                return new CommandResponse<RemoveModeratorResponse>(response, Status.Denied, errorMessage);
            }
        }

        #endregion Moderator Functions

        #region RuleReports
       
        public async Task<IEnumerable<Data.Models.RuleSet>> GetRuleSets(string subverse, ContentType? contentType)
        {
            var typeFilter = contentType == ContentType.Submission ? "r.SubmissionID IS NOT NULL" : "r.CommentID IS NOT NULL";
            var q = new DapperQuery();

            q.Select = "* FROM RuleSet r";
            q.Where = "(r.Subverse = @Subverse OR r.Subverse IS NULL) AND (r.ContentType = @ContentType OR r.ContentType IS NULL) AND r.IsActive = 1";
            q.OrderBy = "r.SortOrder ASC";

            int? intContentType = contentType == null ? (int?)null : (int)contentType;

            var data = await _db.Database.Connection.QueryAsync<Data.Models.RuleSet>(q.ToString(), new { Subverse = subverse, ContentType = intContentType });

            return data;

        }
        public async Task<Dictionary<ContentItem, IEnumerable<ContentUserReport>>> GetRuleReports(string subverse, ContentType? contentType = null, int hours = 24, ReviewStatus reviewedStatus = ReviewStatus.Unreviewed, int[] ruleSetID = null)
        {
            var typeFilter = contentType == ContentType.Submission ? "rr.SubmissionID IS NOT NULL" : "rr.CommentID IS NOT NULL";
            var q = new DapperQuery();

            q.Select = $@"SELECT rr.Subverse, rr.UserName, rr.SubmissionID, rr.CommentID, rr.RuleSetID, r.Name, r.Description, Count = COUNT(*), MostRecent = MAX(rr.CreationDate)
                        FROM RuleReport rr WITH (NOLOCK)
                        INNER JOIN RuleSet r WITH (NOLOCK) ON rr.RuleSetID = r.ID";

                        //--LEFT JOIN Submission s WITH (NOLOCK) ON s.ID = rr.SubmissionID
                        //--LEFT JOIN Comment c WITH (NOLOCK) ON c.ID = rr.CommentID
                        //WHERE
                        //    (rr.Subverse = @Subverse OR @Subverse IS NULL)
                        //    AND
                        //    (rr.CreationDate >= @StartDate OR @StartDate IS NULL)
                        //    AND
                        //    (rr.CreationDate <= @EndDate OR @EndDate IS NULL)
                        //    AND {typeFilter}
            q.Where = @"(rr.Subverse = @Subverse OR @Subverse IS NULL)
                        AND
                        (rr.CreationDate >= @StartDate OR @StartDate IS NULL)
                        AND
                        (rr.CreationDate <= @EndDate OR @EndDate IS NULL)";
            q.OrderBy = "MostRecent DESC";

            if (contentType != null)
            {
                q.Append(x => x.Where, contentType == ContentType.Submission ? "rr.SubmissionID IS NOT NULL" : "rr.CommentID IS NOT NULL");
            }

            if (reviewedStatus != ReviewStatus.Any)
            {
                q.Append(x => x.Where, reviewedStatus == ReviewStatus.Reviewed ? "rr.ReviewedDate IS NOT NULL" : "rr.ReviewedDate IS NULL");
            }

            if (ruleSetID != null && ruleSetID.Any())
            {
                q.Append(x => x.Where, "rr.RuleSetID IN @RuleSetID");
            }

            q.GroupBy = "rr.Subverse, rr.UserName, rr.SubmissionID, rr.CommentID, rr.RuleSetID, r.Name, r.Description";

            DateTime? startDate = Repository.CurrentDate.AddHours(hours * -1);
            DateTime? endDate = null;

            var data = await _db.Database.Connection.QueryAsync<ContentUserReport>(q.ToString(), new { Subverse = subverse, StartDate = startDate, EndDate = endDate, RuleSetID = ruleSetID });

            Dictionary<ContentItem, IEnumerable<ContentUserReport>> groupedData = new Dictionary<ContentItem, IEnumerable<ContentUserReport>>();

            //load target content and add to output dictionary
            if (contentType == null || contentType == ContentType.Submission)
            {
                var ids = data.Where(x => x.SubmissionID != null && x.CommentID == null).Select(x => x.SubmissionID.Value).Distinct();
                //Get associated content
                var submissions = await GetSubmissions(ids.ToArray());
                var dict = ids.ToDictionary(x => new ContentItem() { Submission = DomainMaps.Map(submissions.FirstOrDefault(s => s.ID == x)), ContentType = ContentType.Submission }, x => data.Where(y => y.SubmissionID.Value == x && !y.CommentID.HasValue));
                dict.ToList().ForEach(x => groupedData.Add(x.Key, x.Value));
            }

            if (contentType == null || contentType == ContentType.Comment)
            {
                var ids = data.Where(x => x.SubmissionID != null && x.CommentID != null).Select(x => x.CommentID.Value).Distinct();
                //Get associated content
                var comments = await GetComments(ids.ToArray());
                var dict = ids.ToDictionary(x => new ContentItem() { Comment = comments.FirstOrDefault(s => s.ID == x), ContentType = ContentType.Comment}, x => data.Where(y => y.CommentID.HasValue && y.CommentID.Value == x));
                dict.ToList().ForEach(x => groupedData.Add(x.Key, x.Value));
            }

            return groupedData;

        }
        [Authorize]
        public async Task<CommandResponse> MarkReportsAsReviewed(string subverse, ContentType contentType, int id)
        {

            DemandAuthentication();

            if (!SubverseExists(subverse))
            {
                return CommandResponse.FromStatus(Status.Invalid, "Subverse does not exist");
            }
            if (!ModeratorPermission.HasPermission(User.Identity.Name, subverse, ModeratorAction.MarkReports))
            {
                return CommandResponse.FromStatus(Status.Denied, "User does not have permissions to mark reports");
            }

            var q = new DapperUpdate();
            q.Update = "r SET r.ReviewedBy = @UserName, r.ReviewedDate = @CreationDate FROM RuleReport r";
            if (contentType == ContentType.Submission)
            {
                q.Where = "r.Subverse = @Subverse AND SubmissionID = @ID";
            }
            else
            {
                q.Where = "r.Subverse = @Subverse AND CommentID = @ID";
            }
            q.Append(x => x.Where, "r.ReviewedDate IS NULL AND r.ReviewedBy IS NULL");

            var result = await _db.Database.Connection.ExecuteAsync(q.ToString(), new { Subverse = subverse, ID = id, UserName = User.Identity.Name, CreationDate = CurrentDate });

            return CommandResponse.FromStatus(Status.Success);

        }

        [Authorize]
        public async Task<CommandResponse> SaveRuleReport(ContentType contentType, int id, int ruleID)
        {
            DemandAuthentication();

            var duplicateFilter = "";
            switch (contentType)
            {
                case ContentType.Comment:
                    duplicateFilter = "AND CommentID = @ID";
                    break;
                case ContentType.Submission:
                    duplicateFilter = "AND SubmissionID = @ID AND CommentID IS NULL";
                    break;
                default:
                    throw new NotImplementedException("ContentType not supported");
                    break;
            }

            var q = $"IF NOT EXISTS (SELECT * FROM RuleReport WHERE CreatedBy = @UserName {duplicateFilter}) INSERT RuleReport (Subverse, UserName, SubmissionID, CommentID, RuleSetID, CreatedBy, CreationDate) ";

            switch (contentType)
            {
                case ContentType.Comment:
                    q += @"SELECT s.Subverse, NULL, s.ID, c.ID, @RuleID, @UserName, GETUTCDATE() FROM Submission s WITH (NOLOCK) 
                          INNER JOIN Comment c WITH (NOLOCK) ON c.SubmissionID = s.ID 
                          INNER JOIN RuleSet r WITH (NOLOCK) ON r.ID = @RuleID AND (r.Subverse = s.Subverse OR r.Subverse IS NULL) AND (r.ContentType = @ContentType OR r.ContentType IS NULL) 
                          WHERE c.ID = @ID AND c.IsDeleted = 0 AND r.IsActive = 1";
                    break;
                case ContentType.Submission:
                    q += @"SELECT s.Subverse, NULL, s.ID, NULL, @RuleID, @UserName, GETUTCDATE() FROM Submission s WITH (NOLOCK) 
                        INNER JOIN RuleSet r WITH (NOLOCK) ON r.ID = @RuleID AND (r.Subverse = s.Subverse OR r.Subverse IS NULL) AND (r.ContentType = @ContentType OR r.ContentType IS NULL) 
                        WHERE s.ID = @ID AND s.IsDeleted = 0 AND r.IsActive = 1";
                    break;
            }
            //filter out banned users
            q += " AND NOT EXISTS (SELECT * FROM BannedUser WHERE UserName = @UserName) AND NOT EXISTS(SELECT * FROM SubverseBan WHERE UserName = @UserName AND Subverse = s.Subverse)";

            var result = await _db.Database.Connection.ExecuteAsync(q, new { UserName = User.Identity.Name, ID = id, RuleID = ruleID, ContentType = (int)contentType });

            return CommandResponse.Successful();
        }

        #endregion
        
        #region Admin Functions

        //public void SaveAdminLogEntry(AdminLog log) {
        //    if (log == null){
        //        throw new VoatValidationException("AdminLog can not be null");
        //    }
        //    if (String.IsNullOrEmpty(log.Action)) {
        //        throw new VoatValidationException("AdminLog.Action must have a valid value");
        //    }
        //    if (String.IsNullOrEmpty(log.Type)) {
        //        throw new VoatValidationException("AdminLog.Type must have a valid value");
        //    }
        //    if (String.IsNullOrEmpty(log.Details)) {
        //        throw new VoatValidationException("AdminLog.Details must have a valid value");
        //    }

        //    //Set specific info
        //    log.UserName = User.Identity.Name;
        //    log.CreationDate = CurrentDate;

        //    _db.AdminLogs.Add(log);
        //    _db.SaveChanges();

        //}

        //TODO: Add roles allowed to execute
        //TODO: this method is a multi-set without transaction support. Correct this you hack.
        //[Authorize(Roles="GlobalAdmin,Admin,DelegateAdmin")]
        //public void TransferSubverse(SubverseTransfer transfer) {
        //    if (User.Identity.IsAuthenticated) {
        //        //validate info
        //        string sub = ToCorrectSubverseCasing(transfer.Subverse);
        //        if (String.IsNullOrEmpty(sub) && transfer.IsApproved) {
        //            throw new VoatValidationException("Can not find subverse '{0}'", transfer.Subverse);
        //        }
        //        transfer.Subverse = sub;

        //        string user = ToCorrectUserNameCasing(transfer.UserName);
        //        if (String.IsNullOrEmpty(user)) {
        //            throw new VoatValidationException("Can not find user '{0}'", transfer.UserName);
        //        }
        //        transfer.UserName = user;

        //        if (transfer.IsApproved) {
        //            //Issue transfer // do something with this value later 0 = failed, 1 = success
        //            int success = _db.usp_TransferSubverse(transfer.Subverse, transfer.UserName);
        //        }

        //        //Write Admin Log Entry
        //        AdminLog logEntry = new AdminLog();

        //        //reference info
        //        logEntry.RefUserName = transfer.UserName;
        //        logEntry.RefSubverse = transfer.Subverse;
        //        logEntry.RefUrl = transfer.TransferRequestUrl;
        //        logEntry.RefSubmissionID = transfer.SubmissionID;

        //        logEntry.Type = "SubverseTransfer";
        //        logEntry.Action = (transfer.IsApproved ? "Approved" : "Denied");
        //        logEntry.InternalDetails = transfer.Reason;
        //        logEntry.Details = String.Format("Request to transfer subverse {0} to {1} has been {2}", transfer.Subverse, transfer.UserName, logEntry.Action);

        //        SaveAdminLogEntry(logEntry);

        //        //Send user transfer message
        //        if (!String.IsNullOrEmpty(transfer.MessageToRequestor)) {
        //            if (transfer.SubmissionID > 0) {
        //                PostComment(transfer.SubmissionID, null, String.Format("{0}: {1}", (transfer.IsApproved ? "Approved" : "Denied"), transfer.MessageToRequestor));
        //            } else {
        //                string title = (String.IsNullOrEmpty(transfer.Subverse) ? "Subverse Transfer" : String.Format("/v/{0} Transfer", transfer.Subverse));
        //                SendMessage(new ApiSendUserMessage() { Message = transfer.MessageToRequestor, Recipient = transfer.UserName, Subject = title });
        //            }
        //        }

        //    }
        //}

        #endregion Admin Functions

        #region Block

        /// <summary>
        /// Unblocks a domain type
        /// </summary>
        /// <param name="domainType"></param>
        /// <param name="name"></param>
        public async Task Unblock(DomainType domainType, string name)
        {
            await Block(domainType, name, SubscriptionAction.Unsubscribe);
        }

        /// <summary>
        /// Blocks a domain type
        /// </summary>
        /// <param name="domainType"></param>
        /// <param name="name"></param>
        public async Task Block(DomainType domainType, string name)
        {
            await Block(domainType, name, SubscriptionAction.Subscribe);
        }

        /// <summary>
        /// Blocks, Unblocks, or Toggles blocks
        /// </summary>
        /// <param name="domainType"></param>
        /// <param name="name"></param>
        /// <param name="block">If null then toggles, else, blocks or unblocks based on value</param>
        public async Task<CommandResponse<bool?>> Block(DomainType domainType, string name, SubscriptionAction action)
        {
            DemandAuthentication();

            var response = new CommandResponse<bool?>();

                switch (domainType)
                {
                    case DomainType.Subverse:

                        var exists = _db.Subverses.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                        if (exists == null)
                        {
                            throw new VoatNotFoundException("Subverse '{0}' does not exist", name);
                        }
                        //Add to user Block Set
                        //Set propercased name
                        name = exists.Name;

                        var set = GetOrCreateSubverseSet(new SubverseSet() { Name = SetType.Blocked.ToString(), UserName = User.Identity.Name, Type = (int)SetType.Blocked, Description = "Blocked Subverses" });
                        //var action = block == null ? (SubscriptionAction?)null : (block.Value ? SubscriptionAction.Subscribe : SubscriptionAction.Unsubscribe);

                        response = await SetSubverseListChange(set, exists, action);

                        //var subverseBlock = db.UserBlockedSubverses.FirstOrDefault(n => n.Subverse.ToLower() == name.ToLower() && n.UserName == User.Identity.Name);
                        //if (subverseBlock == null && ((block.HasValue && block.Value) || !block.HasValue))
                        //{
                        //    db.UserBlockedSubverses.Add(new UserBlockedSubverse { UserName = User.Identity.Name, Subverse = name, CreationDate = Repository.CurrentDate });
                        //    response.Response = true;
                        //}
                        //else if (subverseBlock != null && ((block.HasValue && !block.Value) || !block.HasValue))
                        //{
                        //    db.UserBlockedSubverses.Remove(subverseBlock);
                        //    response.Response = false;
                        //}
                        //db.SaveChanges();
                        break;

                    case DomainType.User:

                        //Ensure user exists, get propercased user name
                        name = UserHelper.OriginalUsername(name);
                        if (String.IsNullOrEmpty(name))
                        {
                            return new CommandResponse<bool?>(null, Status.Error, "User does not exist");
                        }
                        var userBlock = _db.UserBlockedUsers.FirstOrDefault(n => n.BlockUser.ToLower() == name.ToLower() && n.UserName == User.Identity.Name);
                        if (userBlock == null && (action == SubscriptionAction.Subscribe || action == SubscriptionAction.Toggle))
                        {
                            _db.UserBlockedUsers.Add(new UserBlockedUser { UserName = User.Identity.Name, BlockUser = name, CreationDate = Repository.CurrentDate });
                            response.Response = true;
                        }
                        else if (userBlock != null && (action == SubscriptionAction.Unsubscribe || action == SubscriptionAction.Toggle))
                        {
                            _db.UserBlockedUsers.Remove(userBlock);
                            response.Response = false;
                        }

                        await _db.SaveChangesAsync();
                        break;

                    default:
                        throw new NotImplementedException(String.Format("Blocking of {0} is not implemented yet", domainType.ToString()));
                        break;
                }
            
            response.Status = Status.Success;
            return response;
        }

        #endregion Block

        #region Misc


        public async Task<string> GetRandomSubverse(bool nsfw)
        {

            var q = new DapperQuery();
            q.Select = "SELECT TOP 1 s.Name FROM Subverse s INNER JOIN Submission sm ON s.Name = sm.Subverse";
            q.Where = @"s.SubscriberCount > 10
                        AND s.Name != 'all'
                        AND s.IsAdult = @IsAdult
                        AND s.IsAdminDisabled = 0";
            q.GroupBy = "s.Name";
            q.Having = "DATEDIFF(HH, MAX(sm.CreationDate), GETUTCDATE()) < @HourLimit";
            q.OrderBy = "NEWID()";
            q.Parameters = new DynamicParameters(new { IsAdult = nsfw, HourLimit = (24 * 7) });


            return await _db.Database.Connection.ExecuteScalarAsync<string>(q.ToString(), q.Parameters);
            /*
            SELECT TOP 1 s.Name FROM Subverse s
            INNER JOIN Submission sm ON s.Name = sm.Subverse
            WHERE 
            s.SubscriberCount > 10
            AND s.Name != 'all'
            AND s.IsAdult = @IsAdult
            AND s.IsAdminDisabled = 0
            GROUP BY s.Name
            HAVING DATEDIFF(HH, MAX(sm.CreationDate), GETUTCDATE()) < (24 * 7)
            ORDER BY NEWID()
            */

        }
        public double? HighestRankInSubverse(string subverse)
        {
            var q = new DapperQuery();
            q.Select = "TOP 1 ISNULL(Rank, 0) FROM Submission WITH (NOLOCK)";
            q.Where = "Subverse = @Subverse AND ArchiveDate IS NULL";
            q.OrderBy = "Rank DESC";
            q.Parameters = new { Subverse = subverse }.ToDynamicParameters();

            var result = _db.Database.Connection.ExecuteScalar<double?>(q.ToString(), q.Parameters);
            return result;

            //using (var db = new voatEntities())
            //{
            //    var submission = db.Submissions.OrderByDescending(x => x.Rank).Where(x => x.Subverse == subverse).FirstOrDefault();
            //    if (submission != null)
            //    {
            //        return submission.Rank;
            //    }
            //    return null;
            //}
        }

        public int VoteCount(string sourceUser, string targetUser, ContentType contentType, Vote voteType, TimeSpan timeSpan)
        {
            var sum = 0;
            var startDate = CurrentDate.Subtract(timeSpan);

            if ((contentType & ContentType.Comment) > 0)
            {
                var count = (from x in _db.CommentVoteTrackers
                             join c in _db.Comments on x.CommentID equals c.ID
                             where
                                 x.UserName.Equals(sourceUser, StringComparison.OrdinalIgnoreCase)
                                 &&
                                 c.UserName.Equals(targetUser, StringComparison.OrdinalIgnoreCase)
                                 &&
                                 x.CreationDate > startDate
                                 &&
                                 (voteType == Vote.None || x.VoteStatus == (int)voteType)
                             select x).Count();
                sum += count;
            }
            if ((contentType & ContentType.Submission) > 0)
            {
                var count = (from x in _db.SubmissionVoteTrackers
                             join s in _db.Submissions on x.SubmissionID equals s.ID
                             where
                                 x.UserName.Equals(sourceUser, StringComparison.OrdinalIgnoreCase)
                                 &&
                                 s.UserName.Equals(targetUser, StringComparison.OrdinalIgnoreCase)
                                 &&
                                 x.CreationDate > startDate
                                 &&
                                 (voteType == Vote.None || x.VoteStatus == (int)voteType)
                             select x).Count();
                sum += count;
            }
            return sum;
        }

        public bool HasAddressVoted(string addressHash, ContentType contentType, int id)
        {
            var result = true;
            switch (contentType)
            {
                case ContentType.Comment:
                    result = _db.CommentVoteTrackers.Any(x => x.CommentID == id && x.IPAddress == addressHash);
                    break;

                case ContentType.Submission:
                    result = _db.SubmissionVoteTrackers.Any(x => x.SubmissionID == id && x.IPAddress == addressHash);
                    break;
            }
            return result;
        }

        private static IQueryable<Models.Submission> ApplySubmissionSearch(SearchOptions options, IQueryable<Models.Submission> query)
        {
            //HACK: Warning, Super hacktastic
            if (!String.IsNullOrEmpty(options.Phrase))
            {
                //WARNING: This is a quickie that views spaces as AND conditions in a search.
                List<string> keywords = null;
                if (options.Phrase.Contains(" "))
                {
                    keywords = new List<string>(options.Phrase.Split(' '));
                }
                else
                {
                    keywords = new List<string>(new string[] { options.Phrase });
                }

                keywords.ForEach(x =>
                {
                    query = query.Where(m => m.Title.Contains(x) || m.Content.Contains(x) || m.Url.Contains(x));
                });
            }

            if (options.StartDate.HasValue)
            {
                query = query.Where(x => x.CreationDate >= options.StartDate.Value);
            }
            if (options.EndDate.HasValue)
            {
                query = query.Where(x => x.CreationDate <= options.EndDate.Value);
            }

            //Search Options
            switch (options.Sort)
            {
                case SortAlgorithm.RelativeRank:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.RelativeRank);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.RelativeRank);
                    }
                    break;

                case SortAlgorithm.Rank:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.Rank);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.Rank);
                    }
                    break;

                case SortAlgorithm.New:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.CreationDate);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.CreationDate);
                    }
                    break;

                case SortAlgorithm.Top:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.UpCount);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.UpCount);
                    }
                    break;

                case SortAlgorithm.Viewed:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.Views);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.Views);
                    }
                    break;

                case SortAlgorithm.Discussed:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.Comments.Count);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.Comments.Count);
                    }
                    break;

                case SortAlgorithm.Active:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.Comments.OrderBy(c => c.CreationDate).FirstOrDefault().CreationDate);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.Comments.OrderBy(c => c.CreationDate).FirstOrDefault().CreationDate);
                    }
                    break;

                case SortAlgorithm.Bottom:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.DownCount);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.DownCount);
                    }
                    break;

                case SortAlgorithm.Intensity:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.UpCount + x.DownCount);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.UpCount + x.DownCount);
                    }
                    break;
            }

            query = query.Skip(options.Index).Take(options.Count);
            return query;
        }

        private static IQueryable<Domain.Models.SubmissionComment> ApplyCommentSearch(SearchOptions options, IQueryable<Domain.Models.SubmissionComment> query)
        {
            if (!String.IsNullOrEmpty(options.Phrase))
            {
                //TODO: This is a hack that views Spaces as AND conditions in a search.
                List<string> keywords = null;
                if (!String.IsNullOrEmpty(options.Phrase) && options.Phrase.Contains(" "))
                {
                    keywords = new List<string>(options.Phrase.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries));
                }
                else
                {
                    keywords = new List<string>(new string[] { options.Phrase });
                }

                keywords.ForEach(x =>
                {
                    query = query.Where(m => m.Content.Contains(x));
                });
            }
            if (options.StartDate.HasValue)
            {
                query = query.Where(x => x.CreationDate >= options.StartDate.Value);
            }
            if (options.EndDate.HasValue)
            {
                query = query.Where(x => x.CreationDate <= options.EndDate.Value);
            }

            //TODO: Implement Depth in Comment Table
            //if (options.Depth > 0) {
            //    query = query.Where(x => 1 == 1);
            //}

            switch (options.Sort)
            {
                case SortAlgorithm.Top:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.UpCount);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.DownCount);
                    }
                    break;

                case SortAlgorithm.New:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.CreationDate);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.CreationDate);
                    }
                    break;

                case SortAlgorithm.Bottom:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => x.DownCount);
                    }
                    else
                    {
                        query = query.OrderByDescending(x => x.DownCount);
                    }
                    break;

                default:
                    if (options.SortDirection == SortDirection.Reverse)
                    {
                        query = query.OrderBy(x => (x.UpCount - x.DownCount));
                    }
                    else
                    {
                        query = query.OrderByDescending(x => (x.UpCount - x.DownCount));
                    }
                    break;
            }

            query = query.Skip(options.Index).Take(options.Count);

            return query;
        }

        public IEnumerable<BannedDomain> GetBannedDomains()
        {
            return (from x in _db.BannedDomains
                    orderby x.CreationDate descending
                    select x).ToList();
        }
        public IEnumerable<BannedDomain> BannedDomains(string[] domains, int? gtldMinimumPartEvaulationCount = 1)
        {

            List<string> alldomains = domains.Where(x => !String.IsNullOrEmpty(x)).ToList();

            if (alldomains.Any())
            {
                if (gtldMinimumPartEvaulationCount != null)
                {
                    int minPartCount = Math.Max(1, gtldMinimumPartEvaulationCount.Value);

                    foreach (var domain in domains)
                    {
                        var pieces = domain.Split('.');
                        if (pieces.Length > minPartCount)
                        {
                            pieces = pieces.Reverse().ToArray();
                            for (int i = pieces.Length - 1; i >= minPartCount; i--)
                            {
                                string newDomain = String.Join(".", pieces.Take(i).Reverse());
                                if (!String.IsNullOrEmpty(newDomain))
                                {
                                    alldomains.Add(newDomain);
                                }
                            }
                        }
                    }
                }

                var q = new DapperQuery();
                q.Select = "* FROM BannedDomain";
                q.Where = "Domain IN @Domains";
                q.Parameters = new { Domains = alldomains.ToArray() }.ToDynamicParameters();

                var bannedDomains = _db.Database.Connection.Query<BannedDomain>(q.ToString(), q.Parameters);
                return bannedDomains;
            }

            //return empty
            return new List<BannedDomain>();
        }
        public string SubverseForComment(int commentID)
        {
            var subname = (from x in _db.Comments
                           where x.ID == commentID
                           select x.Submission.Subverse).FirstOrDefault();
            return subname;
        }

        private IPrincipal User
        {
            get
            {
                return System.Threading.Thread.CurrentPrincipal;
            }
        }

        public bool SubverseExists(string subverse)
        {
            return _db.Subverses.Any(x => x.Name == subverse);
        }

        public string ToCorrectSubverseCasing(string subverse)
        {
            if (!String.IsNullOrEmpty(subverse))
            {
                var sub = _db.Subverses.FirstOrDefault(x => x.Name == subverse);
                return (sub == null ? null : sub.Name);
            }
            else
            {
                return null;
            }
        }

        public string ToCorrectUserNameCasing(string userName)
        {
            if (!String.IsNullOrEmpty(userName))
            {
                return UserHelper.OriginalUsername(userName);
            }
            else
            {
                return null;
            }
        }

        private void DemandAuthentication()
        {
            if (!User.Identity.IsAuthenticated || String.IsNullOrEmpty(User.Identity.Name))
            {
                throw new VoatSecurityException("Current process not authenticated.");
            }
        }

        //TODO: Make async
        public Models.EventLog Log(EventLog log)
        {
            var newLog = _db.EventLogs.Add(log);
            _db.SaveChanges();
            return newLog;
        }

        public static DateTime CurrentDate
        {
            get
            {
                return DateTime.UtcNow;
            }
        }

        public IEnumerable<Data.Models.Filter> GetFilters(bool activeOnly = true)
        {
            var q = new DapperQuery();
            q.Select = "* FROM Filter";
            if (activeOnly)
            {
                q.Where = "IsActive = @IsActive";
            }
            //will return empty list I believe, so should be runtime cacheable 
            return _db.Database.Connection.Query<Data.Models.Filter>(q.ToString(), new { IsActive = activeOnly });
        }

        protected CommandResponse<T> MapRuleOutCome<T>(RuleOutcome outcome, T result)
        {
            switch (outcome.Result)
            {
                case RuleResult.Denied:
                    return CommandResponse.FromStatus(result, Status.Denied, outcome.Message);

                default:
                    return CommandResponse.Successful(result);
            }
        }

        #endregion Misc

        #region Search 

        public async Task<IEnumerable<SubverseSubmissionSetting>> SubverseSubmissionSettingsSearch(string subverseName, bool exactMatch)
        {

            var q = new DapperQuery();
            q.Select = "Name, IsAnonymized, IsAdult FROM Subverse";
            q.OrderBy = "SubscriberCount DESC, CreationDate ASC";

            if (exactMatch)
            {
                q.Where = "Name = @Name";
                q.TakeCount = 1;
            }
            else
            {
                q.Where = "Name LIKE CONCAT(@Name, '%') OR Name = @Name";
                q.TakeCount = 10;
            }

            return await _db.Database.Connection.QueryAsync<SubverseSubmissionSetting>(q.ToString(), new { Name = subverseName });

        }

        #endregion

        #region User

        public async Task<CommandResponse> DeleteAccount(DeleteAccountOptions options)
        {
            DemandAuthentication();

            //if (!User.Identity.Name.IsEqual(model.UserName))
            //{
            //    return RedirectToAction("Manage", new { message = ManageMessageId.UserNameMismatch });
            //}
            //else
            //{
            //    // require users to enter their password in order to execute account delete action
            //    var user = UserManager.Find(User.Identity.Name, model.CurrentPassword);

            //    if (user != null)
            //    {
            //        // execute delete action
            //        if (UserHelper.DeleteUser(User.Identity.Name))
            //        {
            //            // delete email address and set password to something random
            //            UserManager.SetEmail(User.Identity.GetUserId(), null);

            //            string randomPassword = "";
            //            using (SHA512 shaM = new SHA512Managed())
            //            {
            //                randomPassword = Convert.ToBase64String(shaM.ComputeHash(Encoding.UTF8.GetBytes(Path.GetRandomFileName())));
            //            }

            //            UserManager.ChangePassword(User.Identity.GetUserId(), model.CurrentPassword, randomPassword);

            //            AuthenticationManager.SignOut();
            //            return View("~/Views/Account/AccountDeleted.cshtml");
            //        }

            //        // something went wrong when deleting user account
            //        return View("~/Views/Error/Error.cshtml");
            //    }
            //}
            if (!options.UserName.IsEqual(options.ConfirmUserName))
            {
                return CommandResponse.FromStatus(Status.Error, "Confirmation UserName does not match");
            }

            if (User.Identity.Name.IsEqual(options.UserName))
            {

                var userName = User.Identity.Name;

                //ensure banned user blocked from operation
                if (_db.BannedUsers.Any(x => x.UserName.Equals(options.UserName, StringComparison.OrdinalIgnoreCase)))
                {
                    return CommandResponse.FromStatus(Status.Denied, "User is Globally Banned");
                }

                using (var userManager = new UserManager<VoatUser>(new UserStore<VoatUser>(new ApplicationDbContext())))
                {
                    var userAccount = userManager.Find(options.UserName, options.CurrentPassword);
                    if (userAccount != null)
                    {

                        //Verify Email before proceeding
                        var setRecoveryEmail = !String.IsNullOrEmpty(options.RecoveryEmailAddress) && options.RecoveryEmailAddress.IsEqual(options.ConfirmRecoveryEmailAddress);
                        if (setRecoveryEmail)
                        {
                            var userWithEmail = userManager.FindByEmail(options.RecoveryEmailAddress);
                            if (userWithEmail != null && userWithEmail.UserName != userAccount.UserName)
                            {
                                return CommandResponse.FromStatus(Status.Error, "This email address is in use, please provide a unique address");
                            }
                        }

                        List<DapperBase> statements = new List<DapperBase>();
                        var deleteText = "Account Deleted By User";
                        //Comments
                        switch (options.Comments)
                        {
                            case DeleteOption.Anonymize:
                                var a = new DapperUpdate();
                                a.Update = "Update c SET c.IsAnonymized = 1 FROM Comment c WHERE c.UserName = @UserName";
                                a.Parameters = new DynamicParameters(new { UserName = userName });
                                statements.Add(a);
                                break;
                            case DeleteOption.Delete:
                                var d = new DapperUpdate();
                                d.Update = $"Update c SET c.IsDeleted = 1, Content = '{deleteText}' FROM Comment c WHERE c.UserName = @UserName";
                                d.Parameters = new DynamicParameters(new { UserName = userName });
                                statements.Add(d);
                                break;
                        }
                        //Text Submissions
                        switch (options.TextSubmissions)
                        {
                            case DeleteOption.Anonymize:
                                var a = new DapperUpdate();
                                a.Update = $"Update s SET s.IsAnonymized = 1 FROM Submission s WHERE s.UserName = @UserName AND s.Type = {(int)SubmissionType.Text}";
                                a.Parameters = new DynamicParameters(new { UserName = userName });
                                statements.Add(a);
                                break;
                            case DeleteOption.Delete:
                                var d = new DapperUpdate();
                                d.Update = $"Update s SET s.IsDeleted = 1, s.Title = '{deleteText}', s.Content = '{deleteText}' FROM Submission s WHERE s.UserName = @UserName AND s.Type = {(int)SubmissionType.Text}";
                                d.Parameters = new DynamicParameters(new { UserName = userName });
                                statements.Add(d);
                                break;
                        }
                        //Link Submissions
                        switch (options.LinkSubmissions)
                        {
                            case DeleteOption.Anonymize:
                                var a = new DapperUpdate();
                                a.Update = $"Update s SET s.IsAnonymized = 1 FROM Submission s WHERE s.UserName = @UserName AND s.Type = {(int)SubmissionType.Link}";
                                a.Parameters = new DynamicParameters(new { UserName = userName });
                                statements.Add(a);
                                break;
                            case DeleteOption.Delete:
                                var d = new DapperUpdate();
                                d.Update = $"Update s SET s.IsDeleted = 1, s.Title = '{deleteText}', s.Url = 'https://{Settings.SiteDomain}' FROM Submission s WHERE s.UserName = @UserName AND s.Type = {(int)SubmissionType.Link}";
                                d.Parameters = new DynamicParameters(new { UserName = userName });
                                statements.Add(d);
                                break;
                        }

                        // resign from all moderating positions
                        _db.SubverseModerators.RemoveRange(_db.SubverseModerators.Where(m => m.UserName.Equals(options.UserName, StringComparison.OrdinalIgnoreCase)));
                        var u = new DapperDelete();
                        u.Delete = "DELETE m FROM SubverseModerator m";
                        u.Where = "m.UserName = @UserName";
                        u.Parameters = new DynamicParameters(new { UserName = userName });
                        statements.Add(u);

                        //Messages
                        u = new DapperDelete();
                        u.Delete = "DELETE m FROM [Message] m";
                        u.Where = $"((m.Recipient = @UserName AND m.RecipientType = {(int)IdentityType.User} AND m.Type IN @RecipientTypes))";
                        u.Parameters = new DynamicParameters(new
                        {
                            UserName = userName,
                            RecipientTypes = new int[] {
                                (int)MessageType.CommentMention,
                                (int)MessageType.CommentReply,
                                (int)MessageType.SubmissionMention,
                                (int)MessageType.SubmissionReply,
                            }
                        });
                        statements.Add(u);

                        //Start Update Tasks
                        //TODO: Run this in better 
                        //var updateTasks = statements.Select(x => Task.Factory.StartNew(() => { _db.Database.Connection.ExecuteAsync(x.ToString(), x.Parameters); }));

                        foreach (var statement in statements)
                        {
                            await _db.Database.Connection.ExecuteAsync(statement.ToString(), statement.Parameters);
                        }
                    

                    // delete user preferences
                    var userPrefs = _db.UserPreferences.Find(userName);
                        if (userPrefs != null)
                        {
                            // delete short bio
                            userPrefs.Bio = null;

                            // delete avatar
                            if (userPrefs.Avatar != null)
                            {
                                var avatarFilename = userPrefs.Avatar;
                                if (Settings.UseContentDeliveryNetwork)
                                {
                                    // try to delete from CDN
                                    CloudStorageUtility.DeleteBlob(avatarFilename, "avatars");
                                }
                                else
                                {
                                    // try to remove from local FS
                                    string tempAvatarLocation = Settings.DestinationPathAvatars + '\\' + userName + ".jpg";

                                    // the avatar file was not found at expected path, abort
                                    if (FileSystemUtility.FileExists(tempAvatarLocation, Settings.DestinationPathAvatars))
                                    {
                                        File.Delete(tempAvatarLocation);
                                    }
                                    // exec delete
                                }
                                //reset avatar
                                userPrefs.Avatar = null;
                            }
                        }

                        // UNDONE: keep this updated as new features are added (delete sets etc)
                        // username will stay permanently reserved to prevent someone else from registering it and impersonating
                        await _db.SaveChangesAsync().ConfigureAwait(false);

                        //Modify User Account

                        //            UserManager.SetEmail(User.Identity.GetUserId(), null);

                        //            string randomPassword = "";
                        //            using (SHA512 shaM = new SHA512Managed())
                        //            {
                        //                randomPassword = Convert.ToBase64String(shaM.ComputeHash(Encoding.UTF8.GetBytes(Path.GetRandomFileName())));
                        //            }

                        //            UserManager.ChangePassword(User.Identity.GetUserId(), model.CurrentPassword, randomPassword);

                        //            AuthenticationManager.SignOut();
                        //            return View("~/Views/Account/AccountDeleted.cshtml");

                        var userID = userAccount.Id;

                        //Recovery
                        if (setRecoveryEmail)
                        {
                            //Account is recoverable but locked for x days
                            var endLockOutDate = CurrentDate.AddDays(3 * 30);

                            userAccount.Email = options.RecoveryEmailAddress;
                            userAccount.LockoutEnabled = true;
                            userAccount.LockoutEndDateUtc = endLockOutDate;
                            //await userManager.SetEmailAsync(userID, options.RecoveryEmailAddress);
                            //await userManager.SetLockoutEnabledAsync(userID, true);
                            //await userManager.SetLockoutEndDateAsync(userID, CurrentDate.AddDays(3 * 30));
                        }
                        else
                        {
                            userAccount.Email = null;
                            //await userManager.SetEmailAsync(userID, null);
                        }
                        await userManager.UpdateAsync(userAccount).ConfigureAwait(false);

                        //Password
                        string randomPassword = "";
                        using (SHA512 shaM = new SHA512Managed())
                        {
                            randomPassword = Convert.ToBase64String(shaM.ComputeHash(Encoding.UTF8.GetBytes(Path.GetRandomFileName())));
                        }
                        await userManager.ChangePasswordAsync(userID, options.CurrentPassword, randomPassword).ConfigureAwait(false);

                        //await Task.WhenAll(updateTasks).ConfigureAwait(false);

                        return CommandResponse.FromStatus(Status.Success);
                    }
                }
            }
            // user account could not be found
            return CommandResponse.FromStatus(Status.Error, "User Account Not Found");
        }
        #endregion


    }
}